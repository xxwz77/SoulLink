# Offline DOM regression fixture. Builds and uses the embedded page/assets; no BGI service is started.
param([string]$Edge = "${env:ProgramFiles(x86)}/Microsoft/Edge/Application/msedge.exe")
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../SoulLink.TaskEdit.csproj'
$tmp = New-TemporaryFile
Remove-Item -LiteralPath $tmp.FullName
$root = New-Item -ItemType Directory -Path $tmp.FullName
try {
    dotnet build $project -c Release --artifacts-path "$root/build" --nologo
    if ($LASTEXITCODE) { throw 'Build failed' }
    # Read the compiled resources in a .NET 11 process (PowerShell may run on an older runtime).
    $probe = New-Item -ItemType Directory "$root/probe"
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0-windows</TargetFramework></PropertyGroup></Project>' | Set-Content "$probe/Probe.csproj"
    @'
using System;
using System.IO;
using System.Reflection;
var type = Assembly.LoadFrom(args[0]).GetType("SoulLink.TaskEdit.TaskEditStatic", true);
const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
var store = type.Assembly.GetType("SoulLink.TaskEdit.BgiStore", true);
var dir = Path.Combine(args[1],"fixture-install");
Directory.CreateDirectory(Path.Combine(dir,"User","ScriptGroup"));
var group = System.Text.Json.Nodes.JsonNode.Parse("""{"name":"Fixture","projects":[{"type":"Javascript","jsScriptSettingsObject":{"value":"first"}},{"type":"Javascript","jsScriptSettingsObject":{"value":"other"}},{"type":"SoloTask","soloTaskSettings":{"count":5,"unknown":[1]}}]}""");
store.GetMethod("WriteGroup",flags).Invoke(null,new object[]{dir,"Fixture",group,Path.Combine(dir,"backups")});
var reopened = (System.Text.Json.Nodes.JsonNode)store.GetMethod("ReadGroup",flags).Invoke(null,new object[]{dir,"Fixture"});
if (!System.Text.Json.Nodes.JsonNode.DeepEquals(group,reopened)) throw new Exception("Group settings disk round-trip failed");
var api = type.Assembly.GetType("SoulLink.TaskEdit.TaskEditApi", true);
var updateLists = api.GetMethod("GroupTemplateLists", flags);
var patch = System.Text.Json.Nodes.JsonNode.Parse("""{"whiteList":["松茸"],"blackList":["精锻用魔矿"]}""");
updateLists.Invoke(null, new object[]{dir,"Fixture",Path.Combine(dir,"backups"),patch});
var patched = (System.Text.Json.Nodes.JsonNode)store.GetMethod("ReadGroup",flags).Invoke(null,new object[]{dir,"Fixture"});
if (!System.Text.Json.Nodes.JsonNode.DeepEquals(patch,patched["config"]?["autoPickConfig"]) ||
    !System.Text.Json.Nodes.JsonNode.DeepEquals(group["projects"],patched["projects"]))
    throw new Exception("Native group template lists did not round-trip independently");
Console.WriteLine("PASS native group template white and black lists persisted independently");
Console.WriteLine("PASS JS instances and nested solo settings persisted to temporary BGI directory");
var html = (string)type.GetMethod("Page", flags).Invoke(null, null);
foreach (var field in type.GetFields(flags)) {
    if (field.IsLiteral && field.FieldType == typeof(string)) {
        var name = field.Name.ToLowerInvariant();
        var ext = name.EndsWith("js") ? ".js" : ".css";
        var filename = name[..^(ext.Length-1)] + ext;
        File.WriteAllText(Path.Combine(args[1], filename), (string)field.GetRawConstantValue());
    }
}
File.WriteAllText(Path.Combine(args[1], "page.html"), html.Replace("/taskedit/static/", "").Replace("</head>", "<script src='fixture.js'></script></head>"));
'@ | Set-Content "$probe/Program.cs"
    dotnet run --project "$probe/Probe.csproj" -- "$root/build/bin/SoulLink.TaskEdit/release/SoulLink.TaskEdit.dll" "$root"
    if ($LASTEXITCODE) { throw 'Resource probe failed' }
    Copy-Item "$PSScriptRoot/native-fixture.js" "$root/fixture.js"
    $url = ([uri](Join-Path $root.FullName 'page.html')).AbsoluteUri
    $shots = New-Item -ItemType Directory (Join-Path ([IO.Path]::GetTempPath()) ('TaskEdit-Views-' + [guid]::NewGuid().ToString('N')))
    foreach ($view in @(
        @{ Name='desktop'; Size='1600,1000' },
        @{ Name='mobile'; Size='390,844' },
        @{ Name='desktop-group'; Size='1600,1000' },
        @{ Name='mobile-group'; Size='390,844' },
        @{ Name='operations'; Size='1600,1000' },
        @{ Name='picker'; Size='1600,1000' },
        @{ Name='mobile-picker'; Size='390,844' },
        @{ Name='chooser'; Size='900,700' }
    )) {
        $name = $view.Name
        $png = Join-Path $shots.FullName "$name.png"
        $pageUrl = if ($name -eq 'chooser') { $url } else { "${url}?account=Fixture%20User&viewport=$name" }
        # Edge clamps top-level layout width to 492px even when its PNG is 390px wide.
        if ($name.StartsWith('mobile')) {
            @"
<!doctype html><meta charset=utf-8><style>*{margin:0;border:0}iframe{display:block;width:390px;height:844px}</style>
<output id="fixture-result" hidden></output><script>
addEventListener('message', e => { if (e.source === frames[0]) document.querySelector('output').textContent = e.data; });
</script><iframe src="page.html?account=Fixture%20User&amp;viewport=$name"></iframe>
"@ | Set-Content "$root/frame.html"
            $pageUrl = ([uri](Join-Path $root.FullName 'frame.html')).AbsoluteUri
        }
        $argsEdge = @('--headless', '--disable-gpu', '--no-first-run', '--disable-extensions',
            '--disable-background-networking', '--disable-sync', '--force-device-scale-factor=1',
            "--user-data-dir=$root/profile-$name", "--window-size=$($view.Size)", '--virtual-time-budget=5000',
            '--dump-dom', "--screenshot=$png", $pageUrl)
        $dom = & $Edge @argsEdge 2>$null | Out-String
        if ($LASTEXITCODE -or $dom -notmatch '<output id="fixture-result" hidden="">PASS</output>') {
            throw ("$name DOM fixture failed: " + [regex]::Match($dom, '<output[^>]*>[^<]*').Value)
        }
        if (!(Test-Path -LiteralPath $png -PathType Leaf)) { throw "Missing screenshot: $png" }
        Write-Output "PASS $name screenshot: $png"
    }
} finally {
    if ($root.FullName -eq $tmp.FullName) { Remove-Item -Recurse -Force -LiteralPath $root.FullName }
}
