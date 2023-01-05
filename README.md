# AspnetCorePWSH

## Summary
This ASP.NET Core "Shim" provides a fast, responsive way to serve a PowerShell based MVC website. If you are looking for a dead-simple way to execute and serve individual `.ps1` scripts in IIS and do not care about speed, please check out my `PWSHCGI7` project on GitHub

## Installation
Ensure the .Net Core hosting package is installed in IIS

Publish this project to a folder

Point IIS to that folder when making a new site

Change the Application Pool to Unmanaged as this is .Net Core so .Net4.0 CLR etc will just slow everything down

## Variables exposed to PowerShell
### Read-Only Variables
- `$_GET` - use like PHP `$variable = $_GET['somevariable']`
- `$_POST` - use like PHP `$variable = $_POST['somevariable']`
- `$_COOKIE` - use like PHP `$variable = $_COOKIE['somevariable']`
- `$_RAWPOSTDATA` - used to access the RAW body of the POST/PUT/PATCH request
- `$_REQUEST_METHOD` - Will equal something like `GET` or `POST`
- `$_REQUEST_PATH` - Will contain the request path (e.g. `/` , `/home` , `/api/something`)

### Writable Variables
- `$_HEADERS` - This is a dictionary for headers. Call `$_.HEADERS.add("Key","Value")`
- `$_STATUSCODE` - This is an `INT` for the HTTP Response Status Code (e.g. 200 / 404)

## Routing
All web requests get routed to a file called `controller.ps1` under the PwshWeb directory. This framework forces you to adhere to an MVC model. An example of how a controller would work is below:

```powershell
using module ".\modules\Get-ParsedPSHTML"

$templateDir = $PSScriptRoot + "\views\"
$modelDir = $PSScriptRoot + "\models\"
$template = [PSCustomObject]@{
    htmlTemplateFile = "default_layout.html"
    modelScript      = ""
}

switch -Regex ($_REQUEST_PATH) {
    "^\/$" { 
        $template.modelScript = "home.ps1"
    }
    "^\/unauthorized$" { 
        $template.htmlTemplateFile = "401.html"
    }
    "^\/api/authenticate.ps1$"{
        $template.modelScript = "api\authenticate.ps1"
        $template.htmlTemplateFile = ""
        $_HEADERS.add("Content-Type", "application/json")
    }
    Default { 
        $template.htmlTemplateFile = "404.html"
        $template.headers.add("Status", "404 Not Found")
        $_HEADERS.add("Status", "404 Not Found")
        $Global:_STATUSCODE = 404 #Global isn't required but VSCode complains otherwise
    }

if ($template.modelScript -ne "") {
    Invoke-Expression $(Get-Content -Raw -Path ($modelDir + $template.modelScript))
}

if ($template.htmlTemplateFile -ne "") { 
    $response = `
    $(Get-ParsedPSHTML -inputHTML $(Get-Content -Raw -Path ($templateDir + $template.htmlTemplateFile)))
}
else {
    $response = `
        $controllerOutput
}

return $response #return response to ASP.NET Core shim for delivery back to IIS
```

## Static Content (.js / .css / etc)
Put static content that should be available without authentication in the `public` directory under `PwshWeb`