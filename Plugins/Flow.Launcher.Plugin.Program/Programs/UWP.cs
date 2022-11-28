﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Flow.Launcher.Infrastructure;
using Flow.Launcher.Plugin.Program.Logger;
using Rect = System.Windows.Rect;
using Flow.Launcher.Plugin.SharedModels;
using System.Threading.Channels;
using System.Xml;
using Windows.ApplicationModel.Core;

namespace Flow.Launcher.Plugin.Program.Programs
{
    [Serializable]
    public class UWP
    {
        public string Name { get; }
        public string FullName { get; }
        public string FamilyName { get; }
        public string Location { get; set; }

        public Application[] Apps { get; set; } = Array.Empty<Application>();

        //public PackageVersion Version { get; set; }

        public UWP(Package package)
        {
            Location = package.InstalledLocation.Path;
            Name = package.Id.Name;
            FullName = package.Id.FullName;
            FamilyName = package.Id.FamilyName;
            InitAppsInPackage(package);
        }

        private void InitAppsInPackage(Package package)
        {
            var applist = new List<Application>();
            // WinRT
            var appListEntries = package.GetAppListEntries();
            foreach (var app in appListEntries)
            {
                try
                {
                    var tmp = new Application(app, this);
                    applist.Add(tmp);
                }
                catch (Exception e)
                {
                    // TODO Logging
                }
            }
            Apps = applist.ToArray();

            try
            {
                // From powertoys run
                var xmlDoc = GetManifestXml();
                if (xmlDoc == null)
                {
                    return;
                }

                var xmlRoot = xmlDoc.DocumentElement;
                var packageVersion = GetPackageVersionFromManifest(xmlRoot);
                var logoName = logoNameFromNamespace[packageVersion];

                var namespaceManager = new XmlNamespaceManager(xmlDoc.NameTable);
                namespaceManager.AddNamespace("", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
                namespaceManager.AddNamespace("rescap", "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities");
                namespaceManager.AddNamespace("uap10", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10");

                var allowElevationNode = xmlRoot.SelectSingleNode("//*[name()='rescap:Capability' and @Name='allowElevation']", namespaceManager);
                bool packageCanElevate = allowElevationNode != null;

                var appsNode = xmlRoot.SelectSingleNode("//*[name()='Applications']", namespaceManager);
                foreach (var app in Apps)
                {
                    // According to https://learn.microsoft.com/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps#create-a-package-manifest-for-the-sparse-package
                    // and https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-application#attributes
                    var id = app.UserModelId.Split('!')[1];
                    var appNode = appsNode?.SelectSingleNode($"//*[name()='Application' and @Id='{id}']", namespaceManager);
                    if (appNode != null)
                    {
                        app.CanRunElevated = packageCanElevate || Application.IfAppCanRunElevated(appNode, namespaceManager);
                        var visualElement = appNode.SelectSingleNode($"//*[name()='uap:VisualElements']", namespaceManager);
                        var logoUri = visualElement.Attributes[logoName]?.Value;
                        app.InitLogoPathFromUri(logoUri);
                    }
                }
            }
            catch (Exception e)
            {
                // TODO Logging
            }

        }

        private XmlDocument GetManifestXml()
        {
            var manifest = Path.Combine(Location, "AppxManifest.xml");
            try
            {
                var file = File.ReadAllText(manifest);
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(file);
                return xmlDoc;
            }
            catch (FileNotFoundException e)
            {
                ProgramLogger.LogException("UWP", "GetManifestXml", $"{Location}", "AppxManifest.xml not found.", e);
                return null;
            }
            catch (Exception e)
            {
                ProgramLogger.LogException("UWP", "GetManifestXml", $"{Location}", "An unexpected error occured and unable to parse AppxManifest.xml", e);
                return null;
            }
        }

        /// http://www.hanselman.com/blog/GetNamespacesFromAnXMLDocumentWithXPathDocumentAndLINQToXML.aspx
        private PackageVersion GetPackageVersionFromManifest(XmlElement xmlRoot)
        {
            if (xmlRoot != null)
            {

                var namespaces = xmlRoot.Attributes;
                foreach (XmlAttribute ns in namespaces)
                {
                    if (versionFromNamespace.TryGetValue(ns.Value, out var packageVersion))
                    {
                        return packageVersion;
                    }
                }

                ProgramLogger.LogException($"|UWP|GetPackageVersionFromManifest|{Location}" +
                       "|Trying to get the package version of the UWP program, but an unknown UWP appmanifest version  "
                       + $"{FullName} from location {Location} is returned.", new FormatException());
                return PackageVersion.Unknown;
            }
            else
            {
                ProgramLogger.LogException($"|UWP|GetPackageVersionFromManifest|{Location}" +
                       "|Can't parse AppManifest.xml"
                       + $"{FullName} from location {Location} is returned.", new ArgumentNullException(nameof(xmlRoot)));
                return PackageVersion.Unknown;
            }
        }

        private static readonly Dictionary<string, PackageVersion> versionFromNamespace = new()
        {
                {
                    "http://schemas.microsoft.com/appx/manifest/foundation/windows10", PackageVersion.Windows10
                },
                {
                    "http://schemas.microsoft.com/appx/2013/manifest", PackageVersion.Windows81
                },
                {
                    "http://schemas.microsoft.com/appx/2010/manifest", PackageVersion.Windows8
                },
        };

        private static readonly Dictionary<PackageVersion, string> logoNameFromNamespace = new()
        {
            {
                PackageVersion.Windows10, "Square44x44Logo"
            },
            {
                PackageVersion.Windows81, "Square30x30Logo"
            },
            {
                PackageVersion.Windows8, "SmallLogo"
            },
        };

        public static Application[] All()
        {
            var windows10 = new Version(10, 0);
            var support = Environment.OSVersion.Version.Major >= windows10.Major;
            if (support)
            {
                var applications = CurrentUserPackages().AsParallel().SelectMany(p =>
                {
                    UWP u;
                    try
                    {
                        u = new UWP(p);
                    }
#if !DEBUG
                    catch (Exception e)
                    {
                        ProgramLogger.LogException($"|UWP|All|{p.InstalledLocation}|An unexpected error occured and unable to convert Package to UWP for {p.Id.FullName}", e);
                        return Array.Empty<Application>();
                    }
#endif
#if DEBUG //make developer aware and implement handling
                    catch
                    {
                        throw;
                    }
#endif
                    return u.Apps;
                }).ToArray();

                var updatedListWithoutDisabledApps = applications
                    .Where(t1 => !Main._settings.DisabledProgramSources
                        .Any(x => x.UniqueIdentifier == t1.UniqueIdentifier));

                return updatedListWithoutDisabledApps.ToArray();
            }
            else
            {
                return Array.Empty<Application>();
            }
        }

        private static IEnumerable<Package> CurrentUserPackages()
        {
            var u = WindowsIdentity.GetCurrent().User;

            if (u != null)
            {
                var id = u.Value;
                PackageManager m;
                try
                {
                    m = new PackageManager();
                }
                catch
                {
                    // Bug from https://github.com/microsoft/CsWinRT, using Microsoft.Windows.SDK.NET.Ref 10.0.19041.0.
                    // Only happens on the first time, so a try catch can fix it.
                    m = new PackageManager();
                }
                var ps = m.FindPackagesForUser(id);
                ps = ps.Where(p =>
                {
                    bool valid;
                    try
                    {
                        var f = p.IsFramework;
                        var d = p.IsDevelopmentMode;
                        var path = p.InstalledLocation.Path;
                        valid = !f && !d && !string.IsNullOrEmpty(path);
                    }
                    catch (Exception e)
                    {
                        ProgramLogger.LogException("UWP", "CurrentUserPackages", $"id", "An unexpected error occured and "
                                                                                        + $"unable to verify if package is valid", e);
                        return false;
                    }

                    return valid;
                });
                return ps;
            }
            else
            {
                return Array.Empty<Package>();
            }
        }

        private static Channel<byte> PackageChangeChannel = Channel.CreateBounded<byte>(1);

        public static async Task WatchPackageChange()
        {
            if (Environment.OSVersion.Version.Major >= 10)
            {
                var catalog = PackageCatalog.OpenForCurrentUser();
                catalog.PackageInstalling += (_, args) =>
                {
                    if (args.IsComplete)
                        PackageChangeChannel.Writer.TryWrite(default);
                };
                catalog.PackageUninstalling += (_, args) =>
                {
                    if (args.IsComplete)
                        PackageChangeChannel.Writer.TryWrite(default);
                };
                catalog.PackageUpdating += (_, args) =>
                {
                    if (args.IsComplete)
                        PackageChangeChannel.Writer.TryWrite(default);
                };

                while (await PackageChangeChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    await Task.Delay(3000).ConfigureAwait(false);
                    PackageChangeChannel.Reader.TryRead(out _);
                    await Task.Run(Main.IndexUwpPrograms);
                }

            }
        }

        public override string ToString()
        {
            return FamilyName;
        }

        public override bool Equals(object obj)
        {
            if (obj is UWP uwp)
            {
                return FamilyName.Equals(uwp.FamilyName);
            }
            else
            {
                return false;
            }
        }

        public override int GetHashCode()
        {
            return FamilyName.GetHashCode();
        }

        [Serializable]
        public class Application : IProgram
        {
            private string _uid = string.Empty;
            public string UniqueIdentifier { get => _uid; set => _uid = value == null ? string.Empty : value.ToLowerInvariant(); }
            public string DisplayName { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string UserModelId { get; set; } = string.Empty;
            public string BackgroundColor { get; set; } = string.Empty;
            public string Name => DisplayName;
            public string Location { get; set; } = string.Empty;

            public bool Enabled { get; set; } = false;
            public bool CanRunElevated { get; set; } = false;
            public string LogoPath { get; set; } = string.Empty;

            public Application(AppListEntry appListEntry, UWP package)
            {
                UserModelId = appListEntry.AppUserModelId;
                UniqueIdentifier = appListEntry.AppUserModelId;
                DisplayName = appListEntry.DisplayInfo.DisplayName;
                Description = appListEntry.DisplayInfo.Description;
                Location = package.Location;
                Enabled = true;
            }

            public Result Result(string query, IPublicAPI api)
            {
                string title;
                MatchResult matchResult;

                // We suppose Name won't be null
                if (!Main._settings.EnableDescription || Description == null || Name.StartsWith(Description))
                {
                    title = Name;
                    matchResult = StringMatcher.FuzzySearch(query, title);
                }
                else if (Description.StartsWith(Name))
                {
                    title = Description;
                    matchResult = StringMatcher.FuzzySearch(query, Description);
                }
                else
                {
                    title = $"{Name}: {Description}";
                    var nameMatch = StringMatcher.FuzzySearch(query, Name);
                    var desciptionMatch = StringMatcher.FuzzySearch(query, Description);
                    if (desciptionMatch.Score > nameMatch.Score)
                    {
                        for (int i = 0; i < desciptionMatch.MatchData.Count; i++)
                        {
                            desciptionMatch.MatchData[i] += Name.Length + 2; // 2 is ": "
                        }
                        matchResult = desciptionMatch;
                    }
                    else matchResult = nameMatch;
                }

                if (!matchResult.Success)
                    return null;

                var result = new Result
                {
                    Title = title,
                    SubTitle = Main._settings.HideAppsPath ? string.Empty : Location,
                    //Icon = Logo,
                    IcoPath = LogoPath,
                    Score = matchResult.Score,
                    TitleHighlightData = matchResult.MatchData,
                    ContextData = this,
                    Action = e =>
                    {
                        var elevated = (
                            e.SpecialKeyState.CtrlPressed &&
                            e.SpecialKeyState.ShiftPressed &&
                            !e.SpecialKeyState.AltPressed &&
                            !e.SpecialKeyState.WinPressed
                            );

                        bool shouldRunElevated = elevated && CanRunElevated;
                        _ = Task.Run(() => Launch(shouldRunElevated)).ConfigureAwait(false);
                        if (elevated && !shouldRunElevated)
                        {
                            var title = "Plugin: Program";
                            var message = api.GetTranslation("flowlauncher_plugin_program_run_as_administrator_not_supported_message");
                            api.ShowMsg(title, message, string.Empty);
                        }

                        return true;
                    }
                };


                return result;
            }

            public List<Result> ContextMenus(IPublicAPI api)
            {
                var contextMenus = new List<Result>
                {
                    new Result
                    {
                        Title = api.GetTranslation("flowlauncher_plugin_program_open_containing_folder"),
                        Action = _ =>
                        {
                            Main.Context.API.OpenDirectory(Location);

                            return true;
                        },
                        IcoPath = "Images/folder.png"
                    }
                };

                if (CanRunElevated)
                {
                    contextMenus.Add(new Result
                    {
                        Title = api.GetTranslation("flowlauncher_plugin_program_run_as_administrator"),
                        Action = _ =>
                        {
                            Task.Run(() => Launch(true)).ConfigureAwait(false);
                            return true;
                        },
                        IcoPath = "Images/cmd.png"
                    });
                }

                return contextMenus;
            }

            private void Launch(bool elevated=false)
            {
                string command = "shell:AppsFolder\\" + UserModelId;
                command = Environment.ExpandEnvironmentVariables(command.Trim());

                var info = new ProcessStartInfo(command)
                {
                    UseShellExecute = true,
                    Verb = elevated ? "runas" : ""
                };

                Main.StartProcess(Process.Start, info);
            }

            //public Application(AppxPackageHelper.IAppxManifestApplication manifestApp, UWP package)
            //{
            //    // This is done because we cannot use the keyword 'out' along with a property

            //    manifestApp.GetAppUserModelId(out string tmpUserModelId);
            //    manifestApp.GetAppUserModelId(out string tmpUniqueIdentifier);
            //    manifestApp.GetStringValue("DisplayName", out string tmpDisplayName);
            //    manifestApp.GetStringValue("Description", out string tmpDescription);
            //    manifestApp.GetStringValue("BackgroundColor", out string tmpBackgroundColor);
            //    manifestApp.GetStringValue("EntryPoint", out string tmpEntryPoint);

            //    UserModelId = tmpUserModelId;
            //    UniqueIdentifier = tmpUniqueIdentifier;
            //    DisplayName = tmpDisplayName;
            //    Description = tmpDescription;
            //    BackgroundColor = tmpBackgroundColor;
            //    EntryPoint = tmpEntryPoint;

            //    Location = package.Location;

            //    DisplayName = ResourceFromPri(package.FullName, package.Name, DisplayName);
            //    Description = ResourceFromPri(package.FullName, package.Name, Description);
            //    LogoUri = LogoUriFromManifest(manifestApp);
            //    LogoPath = LogoPathFromUri(LogoUri);

            //    Enabled = true;
            //    CanRunElevated = CanApplicationRunElevated();
            //}

            //private bool CanApplicationRunElevated()
            //{
            //    if (EntryPoint == "Windows.FullTrustApplication")
            //    {
            //        return true;
            //    }

            //    var manifest = Location + "\\AppxManifest.xml";
            //    if (File.Exists(manifest))
            //    {
            //        var file = File.ReadAllText(manifest);

            //        if (file.Contains("TrustLevel=\"mediumIL\"", StringComparison.OrdinalIgnoreCase))
            //        {
            //            return true;
            //        }
            //    }

            //    return false;
            //}

            internal static bool IfAppCanRunElevated(XmlNode appNode, XmlNamespaceManager namespaceManager)
            {
                // According to https://learn.microsoft.com/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps#create-a-package-manifest-for-the-sparse-package
                // and https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-application#attributes

                var entryPointNode = appNode.SelectSingleNode($"//*[local-name()='Application' and @EntryPoint]", namespaceManager);
                var trustLevelNode = appNode.SelectSingleNode($"//*[local-name()='Application' and @uap10:TrustLevel]", namespaceManager);

                return entryPointNode?.Attributes["EntryPoint"]?.Value == "Windows.FullTrustApplication" ||
                       trustLevelNode?.Attributes["uap10:TrustLevel"]?.Value == "mediumIL";
            }

            //internal string ResourceFromPri(string packageFullName, string packageName, string rawReferenceValue)
            //{
            //    if (string.IsNullOrWhiteSpace(rawReferenceValue) || !rawReferenceValue.StartsWith("ms-resource:"))
            //        return rawReferenceValue;

            //    var formattedPriReference = FormattedPriReferenceValue(packageName, rawReferenceValue);

            //    var outBuffer = new StringBuilder(128);
            //    string source = $"@{{{packageFullName}? {formattedPriReference}}}";
            //    var capacity = (uint)outBuffer.Capacity;
            //    var hResult = SHLoadIndirectString(source, outBuffer, capacity, IntPtr.Zero);
            //    if (hResult == Hresult.Ok)
            //    {
            //        var loaded = outBuffer.ToString();
            //        if (!string.IsNullOrEmpty(loaded))
            //        {
            //            return loaded;
            //        }
            //        else
            //        {
            //            ProgramLogger.LogException($"|UWP|ResourceFromPri|{Location}|Can't load null or empty result "
            //                                       + $"pri {source} in uwp location {Location}", new NullReferenceException());
            //            return string.Empty;
            //        }
            //    }
            //    else
            //    {
            //        var e = Marshal.GetExceptionForHR((int)hResult);
            //        ProgramLogger.LogException($"|UWP|ResourceFromPri|{Location}|Load pri failed {source} with HResult {hResult} and location {Location}", e);
            //        return string.Empty;
            //    }
            //}

            //public static string FormattedPriReferenceValue(string packageName, string rawPriReferenceValue)
            //{
            //    const string prefix = "ms-resource:";

            //    if (string.IsNullOrWhiteSpace(rawPriReferenceValue) || !rawPriReferenceValue.StartsWith(prefix))
            //        return rawPriReferenceValue;

            //    string key = rawPriReferenceValue.Substring(prefix.Length);
            //    if (key.StartsWith("//"))
            //        return $"{prefix}{key}";

            //    if (!key.StartsWith("/"))
            //    {
            //        key = $"/{key}";
            //    }

            //    if (!key.ToLower().Contains("resources"))
            //    {
            //        key = $"/Resources{key}";
            //    }

            //    return $"{prefix}//{packageName}{key}";
            //}

            internal void InitLogoPathFromUri(string uri)
            {
                // all https://msdn.microsoft.com/windows/uwp/controls-and-patterns/tiles-and-notifications-app-assets
                // windows 10 https://msdn.microsoft.com/en-us/library/windows/apps/dn934817.aspx
                // windows 8.1 https://msdn.microsoft.com/en-us/library/windows/apps/hh965372.aspx#target_size
                // windows 8 https://msdn.microsoft.com/en-us/library/windows/apps/br211475.aspx

                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new ArgumentException("uri");
                }

                string path = Path.Combine(Location, uri);

                var logoPath = TryToFindLogo(uri, path);
                if (String.IsNullOrEmpty(logoPath))
                {
                    var tmp = Path.Combine(Location, "Assets", uri);
                    if (!path.Equals(tmp, StringComparison.OrdinalIgnoreCase))
                    {
                        // TODO: Don't know why, just keep it at the moment
                        // Maybe on older version of Windows 10?
                        // for C:\Windows\MiracastView etc
                        logoPath = TryToFindLogo(uri, tmp);
                        if (!String.IsNullOrEmpty(logoPath))
                        {
                            LogoPath = logoPath;
                        }
                    }
                }
                else
                {
                    LogoPath = logoPath;
                }

                string TryToFindLogo(string uri, string path)
                {
                    var extension = Path.GetExtension(path);
                    if (extension != null)
                    {
                        //if (File.Exists(path))
                        //{
                        //    return path; // shortcut, avoid enumerating files
                        //}

                        var logoNamePrefix = Path.GetFileNameWithoutExtension(uri); // e.g Square44x44
                        var logoDir = Path.GetDirectoryName(path);  // e.g ..\..\Assets
                        if (String.IsNullOrEmpty(logoNamePrefix) || String.IsNullOrEmpty(logoDir) || !Directory.Exists(logoDir))
                        {
                            // Known issue: Edge always triggers it since logo is not at uri
                            ProgramLogger.LogException($"|UWP|LogoPathFromUri|{Location}" +
                               $"|{UserModelId} can't find logo uri for {uri} in package location (logo name or directory not found): {Location}", new FileNotFoundException());
                            return string.Empty;
                        }

                        var files = Directory.EnumerateFiles(logoDir);

                        // Currently we don't care which one to choose
                        // Just ignore all qualifiers
                        // select like logo.[xxx_yyy].png
                        // https://learn.microsoft.com/en-us/windows/uwp/app-resources/tailor-resources-lang-scale-contrast
                        var logos = files.Where(file =>
                            Path.GetFileName(file)?.StartsWith(logoNamePrefix, StringComparison.OrdinalIgnoreCase) ?? false
                            && extension.Equals(Path.GetExtension(file), StringComparison.OrdinalIgnoreCase)
                        );

                        var selected = logos.FirstOrDefault();
                        var closest = selected;
                        int min = int.MaxValue;
                        foreach (var logo in logos)
                        {

                            var imageStream = File.OpenRead(logo);
                            var decoder = BitmapDecoder.Create(imageStream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                            var height = decoder.Frames[0].PixelHeight;
                            var width = decoder.Frames[0].PixelWidth;
                            int pixelCountDiff = Math.Abs(height * width - 1936); // 44*44=1936
                            if (pixelCountDiff < min)
                            {
                                // try to find the closest to 44x44 logo
                                closest = logo;
                                if (pixelCountDiff == 0)
                                    break;  // found 44x44
                                min = pixelCountDiff;
                            }
                        }

                        selected = closest;
                        if (!string.IsNullOrEmpty(selected))
                        {
                            return selected;
                        }
                        else
                        {
                            ProgramLogger.LogException($"|UWP|LogoPathFromUri|{Location}" +
                                                       $"|{UserModelId} can't find logo uri for {uri} in package location (can't find specified logo): {Location}", new FileNotFoundException());
                            return string.Empty;
                        }
                    }
                    else
                    {
                        ProgramLogger.LogException($"|UWP|LogoPathFromUri|{Location}" +
                                                   $"|Unable to find extension from {uri} for {UserModelId} " +
                                                   $"in package location {Location}", new FileNotFoundException());
                        return string.Empty;
                    }
                }
            }


            public ImageSource Logo()
            {
                var logo = ImageFromPath(LogoPath);
                var plated = PlatedImage(logo);  // TODO: maybe get plated directly from app package?

                // todo magic! temp fix for cross thread object
                plated.Freeze();
                return plated;
            }
            private BitmapImage ImageFromPath(string path)
            {
                // TODO: Consider using infrastructure.image.imageloader?
                if (File.Exists(path))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(path);
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                else
                {
                    ProgramLogger.LogException($"|UWP|ImageFromPath|{(string.IsNullOrEmpty(path) ? "Not Avaliable" : path)}" +
                                               $"|Unable to get logo for {UserModelId} from {path} and" +
                                               $" located in {Location}", new FileNotFoundException());
                    return new BitmapImage(new Uri(Constant.MissingImgIcon));
                }
            }

            private ImageSource PlatedImage(BitmapImage image)
            {
                if (!string.IsNullOrEmpty(BackgroundColor) && BackgroundColor != "transparent")
                {
                    var width = image.Width;
                    var height = image.Height;
                    var x = 0;
                    var y = 0;

                    var group = new DrawingGroup();

                    var converted = ColorConverter.ConvertFromString(BackgroundColor);
                    if (converted != null)
                    {
                        var color = (Color)converted;
                        var brush = new SolidColorBrush(color);
                        var pen = new Pen(brush, 1);
                        var backgroundArea = new Rect(0, 0, width, width);
                        var rectabgle = new RectangleGeometry(backgroundArea);
                        var rectDrawing = new GeometryDrawing(brush, pen, rectabgle);
                        group.Children.Add(rectDrawing);

                        var imageArea = new Rect(x, y, image.Width, image.Height);
                        var imageDrawing = new ImageDrawing(image, imageArea);
                        group.Children.Add(imageDrawing);

                        // http://stackoverflow.com/questions/6676072/get-system-drawing-bitmap-of-a-wpf-area-using-visualbrush
                        var visual = new DrawingVisual();
                        var context = visual.RenderOpen();
                        context.DrawDrawing(group);
                        context.Close();
                        const int dpiScale100 = 96;
                        var bitmap = new RenderTargetBitmap(
                            Convert.ToInt32(width), Convert.ToInt32(height),
                            dpiScale100, dpiScale100,
                            PixelFormats.Pbgra32
                        );
                        bitmap.Render(visual);
                        return bitmap;
                    }
                    else
                    {
                        ProgramLogger.LogException($"|UWP|PlatedImage|{Location}" +
                                                   $"|Unable to convert background string {BackgroundColor} " +
                                                   $"to color for {Location}", new InvalidOperationException());

                        return new BitmapImage(new Uri(Constant.MissingImgIcon));
                    }
                }
                else
                {
                    // todo use windows theme as background
                    return image;
                }
            }

            public override string ToString()
            {
                return $"{DisplayName}: {Description}";
            }

            public override bool Equals(object obj)
            {
                if (obj is Application other)
                {
                    return UniqueIdentifier == other.UniqueIdentifier;
                }
                else
                {
                    return false;
                }
            }

            public override int GetHashCode()
            {
                return UniqueIdentifier.GetHashCode();
            }
        }

        public enum PackageVersion
        {
            Windows10,
            Windows81,
            Windows8,
            Unknown
        }

        //[Flags]
        //private enum Stgm : uint
        //{
        //    Read = 0x0,
        //    ShareExclusive = 0x10,
        //    ShareDenyNone = 0x40
        //}

        //private enum Hresult : uint
        //{
        //    Ok = 0x0000,
        //}

        //[DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        //private static extern Hresult SHCreateStreamOnFileEx(string fileName, Stgm grfMode, uint attributes, bool create,
        //    IStream reserved, out IStream stream);

        //[DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        //private static extern Hresult SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, uint cchOutBuf,
        //    IntPtr ppvReserved);

    }
}
