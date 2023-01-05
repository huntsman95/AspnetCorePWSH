using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection.PortableExecutable;
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

    context.Request.EnableBuffering();

    InitialSessionState sessionState = InitialSessionState.CreateDefault();
    sessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Unrestricted;

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


    if (context.Request.ContentType != null && context.Request.ContentType.StartsWith("application/x-www-form-urlencoded"))
    {
        sessionState.Variables.Add(new SessionStateVariableEntry("_POST", context.Request.Form, "HTTP Post String Dictionary", ScopedItemOptions.Constant));
    }

    if (context.Request.ContentType != null && (context.Request.ContentType.StartsWith("application/json") || context.Request.ContentType.StartsWith("application/x-www-form-urlencoded")))
    {
        // Leave the body open so the next middleware can read it.
        using (var reader = new StreamReader(
            context.Request.Body,
            encoding: Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: (int)context.Request.ContentLength,
            leaveOpen: true))
        {
            var body = await reader.ReadToEndAsync();
            // Do some processing with body…

            // Reset the request body stream position so the next middleware can read it
            context.Request.Body.Position = 0;
            sessionState.Variables.Add(new SessionStateVariableEntry("_RAWPOSTDATA", body, "HTTP Post Data", ScopedItemOptions.Constant));
        }
    }

    //Set default status code
    sessionState.Variables.Add(new SessionStateVariableEntry("_STATUSCODE", 200, "Status Code for HTTP Response", ScopedItemOptions.AllScope));


    string OutputBuffer = "";
    string ContentType = "text/html";

    using (PowerShell PowerShellInst = PowerShell.Create(sessionState))
    {
        PowerShellInst.AddScript(path + "\\controller.ps1", true);
        try
        {
            PSDataCollection<PSObject> results = await PowerShellInst.InvokeAsync();

            StringBuilder stringBuilder = new();
            foreach (PSObject obj in results)
            {
                if (obj != null)
                {
                    stringBuilder.AppendLine(obj.ToString());
                }
            }

            if (PowerShellInst.HadErrors && (Environment.GetEnvironmentVariable("PWSHCGI_DEBUG") == "TRUE"))
            {
                foreach (var error in PowerShellInst.Streams.Error)
                {
                    stringBuilder.AppendLine("<div id=\"pwshErrorDiv\" style=\"background:#FFF !important; color:#000 !important;\"><pre>");
                    stringBuilder.AppendLine(error.ScriptStackTrace.ToString());
                    stringBuilder.AppendLine(error.ToString());
                    stringBuilder.AppendLine("</pre></div>");
                }
            }

            OutputBuffer = stringBuilder.ToString();
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

            //Allow us to configure a status code in PWSH
            int PWSHStatusCode = (int)PowerShellInst.Runspace.SessionStateProxy.GetVariable("_STATUSCODE");
            context.Response.StatusCode = PWSHStatusCode;

        }
        catch (Exception ex)
        {
            OutputBuffer = ex.Message;
        }
    }
    // Send PWSH Output to Browser
    await context.Response.WriteAsync(OutputBuffer);

});

app.Run();
