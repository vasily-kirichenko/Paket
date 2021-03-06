﻿using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Paket.Bootstrapper.Tests")]

namespace Paket.Bootstrapper
{
    class Program
    {
        const string PreferNugetCommandArg = "--prefer-nuget";
        const string PreferNugetAppSettingsKey = "PreferNuget";
        const string ForceNugetCommandArg = "--force-nuget";
        const string ForceNugetAppSettingsKey = "ForceNuget";
        const string PrereleaseCommandArg = "prerelease";
        const string PaketVersionEnv = "PAKET.VERSION";
        const string PaketVersionAppSettingsKey = "PaketVersion";
        const string SelfUpdateCommandArg = "--self";
        const string SilentCommandArg = "-s";
        const string NugetSourceArgPrefix = "--nuget-source=";

        static void Main(string[] args)
        {
            Console.CancelKeyPress += CancelKeyPressed;

            var commandArgs = args;
            var preferNuget = false;
            var forceNuget = false;
            if (commandArgs.Contains(PreferNugetCommandArg))
            {
                preferNuget = true;
                commandArgs = args.Where(x => x != PreferNugetCommandArg).ToArray();
            }
            else if (ConfigurationManager.AppSettings[PreferNugetAppSettingsKey] == "true")
            {
                preferNuget = true;
            }
            if (commandArgs.Contains(ForceNugetCommandArg))
            {
                forceNuget = true;
                commandArgs = args.Where(x => x != ForceNugetCommandArg).ToArray();
            }
            else if (ConfigurationManager.AppSettings[ForceNugetAppSettingsKey] == "true")
            {
                forceNuget = true;
            }
            var silent = false;
            if (commandArgs.Contains(SilentCommandArg))
            {
                silent = true;
                commandArgs = args.Where(x => x != SilentCommandArg).ToArray();
            }
            var dlArgs = EvaluateCommandArgs(commandArgs, silent);

            var effectiveStrategy = GetEffectiveDownloadStrategy(dlArgs, preferNuget, forceNuget);

            StartPaketBootstrapping(effectiveStrategy, dlArgs, silent);
        }

        private static void CancelKeyPressed(object sender, ConsoleCancelEventArgs e)
        {
            Console.WriteLine("Bootstrapper cancelled");
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var target = Path.Combine(folder, "paket.exe");

            var exitCode = 1;
            if (File.Exists(target))
            {
                var localVersion = BootstrapperHelper.GetLocalFileVersion(target);
                Console.WriteLine("Detected existing paket.exe ({0}). Cancelling normally.", localVersion);
                exitCode = 0;
            }
            Environment.Exit(exitCode);
        }

        private static void StartPaketBootstrapping(IDownloadStrategy downloadStrategy, DownloadArguments dlArgs, bool silent)
        {
            Action<Exception> handleException = exception =>
            {
                if (!File.Exists(dlArgs.Target))
                    Environment.ExitCode = 1;
                BootstrapperHelper.WriteConsoleError(String.Format("{0} ({1})", exception.Message, downloadStrategy.Name));
            };
            try
            {
                if (!silent)
                {
                    string versionRequested;
                    if (!dlArgs.IgnorePrerelease)
                        versionRequested = "prerelease requested";
                    else if (String.IsNullOrWhiteSpace(dlArgs.LatestVersion))
                        versionRequested = "downloading latest stable";
                    else
                        versionRequested = string.Format("version {0} requested", dlArgs.LatestVersion);

                    Console.WriteLine("Checking Paket version ({0})...", versionRequested);
                }

                var localVersion = BootstrapperHelper.GetLocalFileVersion(dlArgs.Target);

                var latestVersion = dlArgs.LatestVersion;
                if (latestVersion == String.Empty)
                {
                    latestVersion = downloadStrategy.GetLatestVersion(dlArgs.IgnorePrerelease);
                }

                if (dlArgs.DoSelfUpdate)
                {
                    if (!silent)
                        Console.WriteLine("Trying self update");
                    downloadStrategy.SelfUpdate(latestVersion, silent);
                }
                else
                {
                    var currentSemVer = String.IsNullOrEmpty(localVersion) ? new SemVer() : SemVer.Create(localVersion);
                    var latestSemVer = SemVer.Create(latestVersion);
                    if (currentSemVer.CompareTo(latestSemVer) != 0)
                    {
                        downloadStrategy.DownloadVersion(latestVersion, dlArgs.Target, silent);
                        if (!silent)
                            Console.WriteLine("Done.");
                    }
                    else
                    {
                        if (!silent)
                            Console.WriteLine("Paket.exe {0} is up to date.", localVersion);
                    }
                }
            }
            catch (WebException exn)
            {
                var shouldHandleException = true;
                if (!File.Exists(dlArgs.Target))
                {
                    if (downloadStrategy.FallbackStrategy != null)
                    {
                        var fallbackStrategy = downloadStrategy.FallbackStrategy;
                        if (!silent)
                            Console.WriteLine("'{0}' download failed. If using Mono, you may need to import trusted certificates using the 'mozroots' tool as none are contained by default. Trying fallback download from '{1}'.", 
                                downloadStrategy.Name, fallbackStrategy.Name);
                        StartPaketBootstrapping(fallbackStrategy, dlArgs, silent);
                        shouldHandleException = !File.Exists(dlArgs.Target);
                    }
                }
                if (shouldHandleException)
                    handleException(exn);
            }
            catch (Exception exn)
            {
                handleException(exn);
            }
        }

        private static IDownloadStrategy GetEffectiveDownloadStrategy(DownloadArguments dlArgs, bool preferNuget, bool forceNuget)
        {
            var gitHubDownloadStrategy = new GitHubDownloadStrategy(BootstrapperHelper.PrepareWebClient, BootstrapperHelper.PrepareWebRequest, BootstrapperHelper.GetDefaultWebProxyFor);
            var nugetDownloadStrategy = new NugetDownloadStrategy(BootstrapperHelper.PrepareWebClient, BootstrapperHelper.GetDefaultWebProxyFor, dlArgs.Folder, dlArgs.NugetSource);

            IDownloadStrategy effectiveStrategy;
            if (forceNuget)
            {
                effectiveStrategy = nugetDownloadStrategy;
                nugetDownloadStrategy.FallbackStrategy = null;
            }
            else if (preferNuget)
            {
                effectiveStrategy = nugetDownloadStrategy;
                nugetDownloadStrategy.FallbackStrategy = gitHubDownloadStrategy;
            }
            else
            {
                effectiveStrategy = gitHubDownloadStrategy;
                gitHubDownloadStrategy.FallbackStrategy = nugetDownloadStrategy;
            }
            return effectiveStrategy;
        }

        private static DownloadArguments EvaluateCommandArgs(string[] args, bool silent)
        {
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var target = Path.Combine(folder, "paket.exe");
            string nugetSource = null;

            var latestVersion = ConfigurationManager.AppSettings[PaketVersionAppSettingsKey] ?? Environment.GetEnvironmentVariable(PaketVersionEnv) ?? String.Empty;
            var ignorePrerelease = true;
            bool doSelfUpdate = false;
            var commandArgs = args;

            if (commandArgs.Contains(SelfUpdateCommandArg))
            {
                commandArgs = commandArgs.Where(x => x != SelfUpdateCommandArg).ToArray();
                doSelfUpdate = true;
            }
            var nugetSourceArg = commandArgs.SingleOrDefault(x => x.StartsWith(NugetSourceArgPrefix));
            if (nugetSourceArg != null)
            {
                commandArgs = commandArgs.Where(x => !x.StartsWith(NugetSourceArgPrefix)).ToArray();
                nugetSource = nugetSourceArg.Substring(NugetSourceArgPrefix.Length);
            }
            if (commandArgs.Length >= 1)
            {
                if (commandArgs[0] == PrereleaseCommandArg)
                {
                    ignorePrerelease = false;
                    latestVersion = String.Empty;
                }
                else
                {
                    latestVersion = commandArgs[0];
                }
            }

            return new DownloadArguments(latestVersion, ignorePrerelease, folder, target, doSelfUpdate, nugetSource);
        }
    }
}
