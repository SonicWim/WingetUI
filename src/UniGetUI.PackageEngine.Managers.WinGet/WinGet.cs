using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UniGetUI.Core;
using UniGetUI.Core.Data;
using UniGetUI.PackageEngine.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using Windows.Graphics.Display;


namespace UniGetUI.PackageEngine.Managers.WingetManager
{
    public class WinGet : PackageManager
    {
        new public static string[] FALSE_PACKAGE_NAMES = new string[] { "", "e(s)", "have", "the", "Id" };
        new public static string[] FALSE_PACKAGE_IDS = new string[] { "", "e(s)", "have", "an", "'winget", "pin'", "have", "an", "Version" };
        new public static string[] FALSE_PACKAGE_VERSIONS = new string[] { "", "have", "an", "'winget", "pin'", "have", "an", "Version" };
        private LocalWingetSource LocalPcSource { get; set; }
        private LocalWingetSource AndroidSubsystemSource { get; set; }
        private LocalWingetSource SteamSource { get; set; }
        private LocalWingetSource UbisoftConnectSource { get; set; }
        private LocalWingetSource GOGSource { get; set; }
        private LocalWingetSource MicrosoftStoreSource { get; set; }

        public WinGet(): base()
        {
            Capabilities = new ManagerCapabilities()
            {
                CanRunAsAdmin = true,
                CanSkipIntegrityChecks = true,
                CanRunInteractively = true,
                SupportsCustomVersions = true,
                SupportsCustomArchitectures = true,
                SupportedCustomArchitectures = new Architecture[] { Architecture.X86, Architecture.X64, Architecture.Arm64 },
                SupportsCustomScopes = true,
                SupportsCustomLocations = true,
                SupportsCustomSources = true,
                SupportsCustomPackageIcons = true,
                SupportsCustomPackageScreenshots = true,
                Sources = new ManagerSource.Capabilities()
                {
                    KnowsPackageCount = false,
                    KnowsUpdateDate = true,
                    MustBeInstalledAsAdmin = true,
                }
            };

            Properties = new ManagerProperties()
            {
                Name = "Winget",
                Description = CoreTools.Translate("Microsoft's official package manager. Full of well-known and verified packages<br>Contains: <b>General Software, Microsoft Store apps</b>"),
                IconId = "winget",
                ColorIconId = "winget_color",
                ExecutableFriendlyName = "winget.exe",
                InstallVerb = "install",
                UninstallVerb = "uninstall",
                UpdateVerb = "update",
                ExecutableCallArgs = "",
                KnownSources = [ new(this, "winget", new Uri("https://cdn.winget.microsoft.com/cache")),
                                 new(this, "msstore", new Uri("https://storeedgefd.dsx.mp.microsoft.com/v9.0")) ],
                DefaultSource = new(this, "winget", new Uri("https://cdn.winget.microsoft.com/cache"))
            };

            SourceProvider = new WinGetSourceProvider(this);
            PackageDetailsProvider = new WinGetPackageDetailsProvider(this);

            LocalPcSource = new LocalWingetSource(this, CoreTools.Translate("Local PC"), "localpc");
            AndroidSubsystemSource = new(this, CoreTools.Translate("Android Subsystem"), "android");
            SteamSource = new(this, "Steam", "steam");
            UbisoftConnectSource = new(this, "Ubisoft Connect", "uplay");
            GOGSource = new(this, "GOG", "gog");
            MicrosoftStoreSource = new(this, "Microsoft Store", "msstore");
        }

        protected override async Task<Package[]> FindPackages_UnSafe(string query)
        {
            return await WinGetHelper.Instance.FindPackages_UnSafe(this, query);
        }

        protected override async Task<UpgradablePackage[]> GetAvailableUpdates_UnSafe()
        {
            List<UpgradablePackage> Packages = new();

            Process p = new();
            p.StartInfo = new ProcessStartInfo()
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            p.Start();

            await p.StandardInput.WriteAsync(@"
                Set-ExecutionPolicy Bypass -Scope Process -Force
                function Print-WinGetPackage {
                    param (
                        [Parameter(Mandatory,ValueFromPipelineByPropertyName)] [string] $Name,
                        [Parameter(Mandatory,ValueFromPipelineByPropertyName)] [string] $Id,
                        [Parameter(Mandatory,ValueFromPipelineByPropertyName)] [string] $InstalledVersion,
                        [Parameter(ValueFromPipelineByPropertyName)] [string[]] $AvailableVersions,
                        [Parameter(Mandatory,ValueFromPipelineByPropertyName)] [bool] $IsUpdateAvailable,
                        [Parameter(ValueFromPipelineByPropertyName)] [string] $Source
                    )
                    process {
                        if($IsUpdateAvailable)
                        {
                            Write-Output(""#"" + $Name + ""`t"" + $Id + ""`t"" + $InstalledVersion + ""`t"" + $AvailableVersions[0] + ""`t"" + $Source)
                        }
                    }
                }

                if(!(Get-Command -Verb Get -Noun WinGetPackage))
                {
                    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Confirm:$false
                    Install-Module -Name Microsoft.WinGet.Client -Scope CurrentUser -AllowClobber -Confirm:$false -Force
                }
                Get-WinGetPackage | Print-WinGetPackage
                

                exit

                ");

            string? line;
            string output = "";
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                output += line + "\n";
                if (!line.StartsWith("#"))
                    continue; // The PowerShell script appends a '#' to the beginning of each line to identify the output

                string[] elements = line.Split('\t');
                if (elements.Length < 5)
                    continue;

                ManagerSource source = GetSourceOrDefault(elements[4]);

                Packages.Add(new UpgradablePackage(elements[0][1..], elements[1], elements[2], elements[3], source, this));
            }

            output += await p.StandardError.ReadToEndAsync();
            LogOperation(p, output);
            await p.WaitForExitAsync();

            return Packages.ToArray();
        }

        protected override async Task<Package[]> GetInstalledPackages_UnSafe()
        {
            List<Package> Packages = new();

            Process p = new();
            p.StartInfo = new ProcessStartInfo()
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            p.Start();

            await p.StandardInput.WriteAsync(@"
                Set-ExecutionPolicy Bypass -Scope Process -Force
                function Print-WinGetPackage {
                    param (
                        [Parameter(Mandatory,ValueFromPipelineByPropertyName)] [string] $Name,
                        [Parameter(Mandatory,ValueFromPipelineByPropertyName)] [string] $Id,
                        [Parameter(Mandatory,ValueFromPipelineByPropertyName)] [string] $InstalledVersion,
                        [Parameter(ValueFromPipelineByPropertyName)] [string[]] $AvailableVersions,
                        [Parameter(Mandatory,ValueFromPipelineByPropertyName)] [bool] $IsUpdateAvailable,
                        [Parameter(ValueFromPipelineByPropertyName)] [string] $Source
                    )
                    process {
                        Write-Output(""#"" + $Name + ""`t"" + $Id + ""`t"" + $InstalledVersion + ""`t"" + $Source)
                    }
                }

                if(!(Get-Command -Verb Get -Noun WinGetPackage))
                {
                    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Confirm:$false
                    Install-Module -Name Microsoft.WinGet.Client -Scope CurrentUser -AllowClobber -Confirm:$false -Force
                }
                Get-WinGetPackage | Print-WinGetPackage
                
                exit
                ");

            string? line;
            string output = "";
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                output += line + "\n";
                if (!line.StartsWith("#"))
                    continue; // The PowerShell script appends a '#' to the beginning of each line to identify the output

                string[] elements = line.Split('\t');
                if (elements.Length < 4)
                    continue;

                ManagerSource source;
                if (elements[3] != "")
                    source = GetSourceOrDefault(elements[3]);
                else
                    source = GetLocalSource(elements[1]);

                Packages.Add(new Package(elements[0][1..], elements[1], elements[2], source, this));
            }

            output += await p.StandardError.ReadToEndAsync();
            LogOperation(p, output);
            await p.WaitForExitAsync();

            return Packages.ToArray();
        }

        private ManagerSource GetLocalSource(string id)
        {
            try
            {
                // Check if source is android
                bool AndroidValid = true;
                foreach (char c in id)
                    if (!"abcdefghijklmnopqrstuvwxyz.".Contains(c))
                    {
                        AndroidValid = false;
                        break;
                    }
                if (AndroidValid && id.Count(x => x == '.') >= 2)
                    return AndroidSubsystemSource;

                // Check if source is Steam
                if ((id == "Steam" || id.Contains("Steam App ")) && id.Split("Steam App").Count() >= 2 && id.Split("Steam App")[1].Trim().Count(x => !"1234567890".Contains(x)) == 0)
                    return SteamSource;

                // Check if source is Ubisoft Connect
                if (id == "Uplay" || id.Contains("Uplay Install ") && id.Split("Uplay Install").Count() >= 2 && id.Split("Uplay Install")[1].Trim().Count(x => !"1234567890".Contains(x)) == 0)
                    return UbisoftConnectSource;

                // Check if source is GOG
                if (id.EndsWith("_is1") && id.Split("_is1")[0].Count(x => !"1234567890".Contains(x)) == 0)
                    return GOGSource;

                // Check if source is Microsoft Store
                if (id.Count(x => x == '_') == 1 && (id.Split('_')[^1].Length == 14 | id.Split('_')[^1].Length == 13))
                    return MicrosoftStoreSource;

                // Otherwise, Source is localpc
                return LocalPcSource;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not parse local source for package {id}");
                Logger.Warn(ex);
                return LocalPcSource;
            }
        }

        public override string[] GetInstallParameters(Package package, InstallationOptions options)
        {
            List<string> parameters = GetUninstallParameters(package, options).ToList();
            parameters[0] = Properties.InstallVerb;

            parameters.Add("--accept-package-agreements");

            if (options.SkipHashCheck)
                parameters.Add("--ignore-security-hash");

            if (options.CustomInstallLocation != "")
            {
                parameters.Add("--location"); parameters.Add("\"" + options.CustomInstallLocation + "\"");
            }

            switch (options.Architecture)
            {
                case (null):
                    break;
                case (Architecture.X86):
                    parameters.Add("--architecture"); parameters.Add("x86");
                    break;
                case (Architecture.X64):
                    parameters.Add("--architecture"); parameters.Add("x64");
                    break;
                case (Architecture.Arm64):
                    parameters.Add("--architecture"); parameters.Add("arm64");
                    break;
            }
            return parameters.ToArray();
        }
        public override string[] GetUpdateParameters(Package package, InstallationOptions options)
        {
            if (package.Name.Contains("64-bit") || package.Id.ToLower().Contains("x64"))
                options.Architecture = Architecture.X64;
            else if (package.Name.Contains("32-bit") || package.Id.ToLower().Contains("x86"))
                options.Architecture = Architecture.X86;

            string[] parameters = GetInstallParameters(package, options);
            parameters[0] = Properties.UpdateVerb;
            List<string> p = parameters.ToList();
            p.Add("--force");
            p.Add("--include-unknown");
            parameters = p.ToArray();
            return parameters;
        }

        public override string[] GetUninstallParameters(Package package, InstallationOptions options)
        {
            List<string> parameters = new() { Properties.UninstallVerb };
            parameters.AddRange(new string[] { "--id", package.Id, "--exact" });
            if(!package.Source.IsVirtualManager)
                parameters.AddRange(new string[] { "--source", package.Source.Name });

            parameters.Add("--accept-source-agreements");

            switch (options.InstallationScope)
            {
                case (PackageScope.Local):
                    parameters.Add("--scope"); parameters.Add("user");
                    break;
                case (PackageScope.Global):
                    parameters.Add("--scope"); parameters.Add("machine");
                    break;
            }

            if (options.Version != "")
                parameters.AddRange(new string[] { "--version", options.Version, "--force" });

            if (options.InteractiveInstallation)
                parameters.Add("--interactive");
            else
                parameters.AddRange(new string[] { "--silent", "--disable-interactivity" });

            return parameters.ToArray();
        }

        public override OperationVeredict GetInstallOperationVeredict(Package package, InstallationOptions options, int ReturnCode, string[] Output)
        {
            string output_string = string.Join("\n", Output);

            if (ReturnCode == -1978334967) // Use https://www.rapidtables.com/convert/number/hex-to-decimal.html for easy UInt(hex) to Int(dec) conversion
                return OperationVeredict.Succeeded; // TODO: Needs restart
            else if (ReturnCode == -1978335215)
                return OperationVeredict.Failed; // TODO: Needs skip checksum

            if (output_string.Contains("No applicable upgrade found") || output_string.Contains("No newer package versions are available from the configured sources"))
                return OperationVeredict.Succeeded;

            if (output_string.Contains("winget settings --enable InstallerHashOverride"))
            {
                Logger.Info("Enabling skip hash ckeck for winget...");
                Process p = new()
                {
                    StartInfo = new ProcessStartInfo()
                    {
                        // FileName = AppTools.GSudoPath,
                        Arguments = Status.ExecutablePath + " " + Properties.ExecutableCallArgs + " settings --enable InstallerHashOverride",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    }
                };
                p.Start();
                p.WaitForExit();
                return OperationVeredict.AutoRetry;
            }

            return ReturnCode == 0 ? OperationVeredict.Succeeded : OperationVeredict.Failed;
        }

        public override OperationVeredict GetUpdateOperationVeredict(Package package, InstallationOptions options, int ReturnCode, string[] Output)
        {
            return GetInstallOperationVeredict(package, options, ReturnCode, Output);
        }

        public override OperationVeredict GetUninstallOperationVeredict(Package package, InstallationOptions options, int ReturnCode, string[] Output)
        {
            string output_string = string.Join("\n", Output);

            if (output_string.Contains("1603") || output_string.Contains("0x80070005") || output_string.Contains("Access is denied"))
            {
                options.RunAsAdministrator = true;
                return OperationVeredict.AutoRetry;
            }

            return ReturnCode == 0 ? OperationVeredict.Succeeded : OperationVeredict.Failed;
        }

        protected override async Task<ManagerStatus> LoadManager()
        {
            ManagerStatus status = new();

            var which_res = await CoreTools.Which("winget.exe");
            status.ExecutablePath = which_res.Item2;
            status.Found = which_res.Item1;

            if (!status.Found)
                return status;

            Process process = new()
            {
                StartInfo = new ProcessStartInfo()
                {
                    FileName = status.ExecutablePath,
                    Arguments = Properties.ExecutableCallArgs + " --version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };
            process.Start();
            status.Version = "Naive WinGet CLI Version: " + (await process.StandardOutput.ReadToEndAsync()).Trim();

            try
            {
                await Task.Run(() => WinGetHelper.Instance = new NativeWinGetHelper());
                status.Version += "\nUsing Native WinGet helper (COM Api)";
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot create native WinGet instance due to error: " + ex.ToString());
                WinGetHelper.Instance = new BundledWinGetHelper();
                status.Version += "\nUsing bundled WinGet helper (CLI parsing)";
            }

            return status;
        }

        public override async Task RefreshPackageIndexes()
        {
            Process p = new();
            p.StartInfo = new ProcessStartInfo()
            {
                FileName = Status.ExecutablePath,
                Arguments = Properties.ExecutableCallArgs + " source update",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            p.Start();
            await p.WaitForExitAsync();
        }
    }

    internal class LocalWingetSource: ManagerSource
    {
        private string name;
        private string __icon_id;
        public override string IconId { get { return __icon_id; } }

        public LocalWingetSource(WinGet manager, string name, string iconId) 
            : base(manager, name, new Uri("https://microsoft.com/local-pc-source"), isVirtualManager: true)
        {
            this.name = name;
            __icon_id = iconId;
        }

        public override string ToString()
        {
            return name;
        }
    }
}




