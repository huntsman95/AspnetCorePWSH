using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Namotion.Reflection;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection.PortableExecutable;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

//app.UseStaticFiles(); //wwwroot

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
           Path.Combine(builder.Environment.ContentRootPath, "PwshWeb\\public")),
    RequestPath = ""
});

var path = Path.Combine(
    builder.Environment.ContentRootPath,
    "PwshWeb");

//Our whole app is basically middleware
app.Run(async (context) => {

    //Allow us to read the POST data in raw format
    context.Request.EnableBuffering();

    //Define session state for PWSH
    InitialSessionState sessionState = InitialSessionState.CreateDefault();
    sessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Unrestricted; //This will fail if you try and containerize the app. Remove this line if so.

    //Expose raw context object to PWSH for advanced read functions
    sessionState.Variables.Add(new SessionStateVariableEntry("_HTTPCONTEXT", context, "HTTP Query String Dictionary", ScopedItemOptions.Constant));

    //Expose Query String to PWSH like PHP
    sessionState.Variables.Add(new SessionStateVariableEntry("_GET", context.Request.Query, "HTTP Query String Dictionary", ScopedItemOptions.Constant));

    //Expose Cookies to PWSH like PHP
    sessionState.Variables.Add(new SessionStateVariableEntry("_COOKIE", context.Request.Cookies, "HTTP Cookies Dictionary", ScopedItemOptions.Constant));

    //Create Headers Dictionary in PWSH so we have control over which headers to send back
    var HeaderDict = new Dictionary<string, string>();
    sessionState.Variables.Add(new SessionStateVariableEntry("_HEADERS", HeaderDict, "HTTP Header Dictionary", ScopedItemOptions.AllScope));

    //Expose Path to Controller
    sessionState.Variables.Add(new SessionStateVariableEntry("_REQUEST_PATH", context.Request.Path, "HTTP Path String", ScopedItemOptions.Constant));
    sessionState.Variables.Add(new SessionStateVariableEntry("_REQUEST_METHOD", context.Request.Method, "HTTP Method String", ScopedItemOptions.Constant));

    //Easily access form data in PWSH using $_POST['key']
    if (context.Request.ContentType != null && context.Request.ContentType.StartsWith("application/x-www-form-urlencoded"))
    {
        sessionState.Variables.Add(new SessionStateVariableEntry("_POST", context.Request.Form, "HTTP Post String Dictionary", ScopedItemOptions.Constant));
    }

    //Write raw JSON data to PWSH to allow us to host REST API's etc
    if (context.Request.ContentType != null && context.Request.ContentType.StartsWith("application/json"))
    {
        using (var reader = new StreamReader(
            context.Request.Body,
            encoding: Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: (int)context.Request.ContentLength,
            leaveOpen: true))
        {
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            sessionState.Variables.Add(new SessionStateVariableEntry("_JSONDATA", body, "HTTP JSON Post Data", ScopedItemOptions.Constant));
        }
    }

    //Set Microsoft.AspNetCore.WebUtilities.FileBufferingReadStream $_RAWPOSTDATASTREAM in PWSH to allow us to parse file uploads etc with our own code
    if (null != context.Request.Body)
    {
        sessionState.Variables.Add(new SessionStateVariableEntry("_RAWPOSTDATASTREAM", context.Request.Body, "HTTP Post Data Stream", ScopedItemOptions.None)); //try to pipe stream directly to PWSH
    }

    //Set default status code of 200 OK
    sessionState.Variables.Add(new SessionStateVariableEntry("_STATUSCODE", 200, "Status Code for HTTP Response", ScopedItemOptions.AllScope));

    //Instantiate Binary Data Byte Array
    sessionState.Variables.Add(new SessionStateVariableEntry("_BINARYRESPONSE", new byte[0], "Status Code for HTTP Response", ScopedItemOptions.AllScope));


    string OutputBuffer = "";
    string ContentType = "text/html";
    byte[] PWSHBinaryResponse = new byte[0];

    List<byte> tmpBytes = new();

    using (PowerShell PowerShellInst = PowerShell.Create(sessionState))
    {
        PowerShellInst.AddScript(path + "\\controller.ps1", true);
        try
        {
            PSDataCollection<PSObject> results = await PowerShellInst.InvokeAsync();

            StringBuilder stringBuilder = new();
            foreach (PSObject obj in results)
            {
                if(null != obj && obj.BaseObject is byte) //allow easy output of binary data - note: this is SLOW; use 
                {
                    tmpBytes.Add((byte)obj.BaseObject);
                }
                else if (null != obj)
                {
                    stringBuilder.AppendLine(obj.ToString());                    
                }
            }

            StringBuilder errorBuilder = new();
            if (PowerShellInst.HadErrors && (Environment.GetEnvironmentVariable("DEBUG") == "TRUE"))
            {
                foreach (var error in PowerShellInst.Streams.Error)
                {
                    errorBuilder.AppendLine(error.ToString());
                    errorBuilder.AppendLine(error.ScriptStackTrace.ToString());
                }
            }

            PWSHBinaryResponse = (byte[])PowerShellInst.Runspace.SessionStateProxy.GetVariable("_BINARYRESPONSE");

            Dictionary<string, string> PWSHHeaders = (Dictionary<string,string>)PowerShellInst.Runspace.SessionStateProxy.GetVariable("_HEADERS");

            if (PWSHHeaders != null)
            {
                foreach(var header in PWSHHeaders)
                {
                    context.Response.Headers.Add(header.Key,header.Value);
                }
                if (!PWSHHeaders.ContainsKey("Content-Type"))
                {
                    context.Response.Headers.Add("Content-Type", ContentType);
                }
            }

            //If content is HTML output formatted errors else just output errors
            if (errorBuilder.Length > 0)
            {
                if (context.Response.Headers.ContainsKey("Content-Type") && (context.Response.Headers["Content-Type"].ToString().ToUpper() == "TEXT/HTML"))
                {
                    stringBuilder.AppendLine("<div id=\"pwshErrorDiv\" style=\"background:#FFF !important; color:#000 !important;\"><pre>");
                    stringBuilder.AppendLine(errorBuilder.ToString());
                    stringBuilder.AppendLine("</pre></div>");
                }
                else
                {
                    stringBuilder.AppendLine(errorBuilder.ToString());
                }
            }

            //Allow us to configure a status code in PWSH
            int PWSHStatusCode = (int)PowerShellInst.Runspace.SessionStateProxy.GetVariable("_STATUSCODE");
            context.Response.StatusCode = PWSHStatusCode;

            //Write script output to buffer
            OutputBuffer = stringBuilder.ToString();
        }
        catch (Exception ex)
        {
            // If we encounter an ASP.Net Core error vs a PWSH error, write it to the web-browser.
            context.Response.StatusCode = 500; //Tell the web browser we encountered an error
            OutputBuffer = ex.Message;
        }
    }
    // Send PWSH Output to Browser

    if (PWSHBinaryResponse.Length > 0) //Fast binary output with PS Variable
    {
        await context.Response.Body.WriteAsync(PWSHBinaryResponse,0,PWSHBinaryResponse.Length);
    }
    else if (tmpBytes.Count > 0) //Slow binary output with PS Obj List but easier to script if you don't read the documentation
    {
        await context.Response.Body.WriteAsync(tmpBytes.ToArray(), 0, tmpBytes.Count);
    }
    else //If we are writing string data (default)
    {
        await context.Response.WriteAsync(OutputBuffer);
    }

});

app.Run();
