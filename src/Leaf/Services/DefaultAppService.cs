using Microsoft.Win32;
using Windows.System;

namespace Leaf.Services;

/// <summary>
/// Knows whether Leaf is registered as a PDF handler (the installer writes the keys) and whether it is the user's
/// default, and opens the Windows Settings page where the user can make it the default with one click.
/// Windows 11 does not allow apps to change the default silently.
/// </summary>
public static class DefaultAppService
{
    public const string ProgId = "Leaf.Document";
    public const string RegisteredName = "Leaf";

    private static readonly string DismissFlag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leaf", "default-prompt-dismissed");

    /// <summary>True when the installer's registration keys exist (per-user or per-machine).</summary>
    public static bool IsRegistered()
    {
        try
        {
            using RegistryKey? user = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications");
            if (user?.GetValue(RegisteredName) is string)
            {
                return true;
            }

            using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(@"Software\RegisteredApplications");
            return machine?.GetValue(RegisteredName) is string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsRegisteredPerMachine()
    {
        try
        {
            using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(@"Software\RegisteredApplications");
            return machine?.GetValue(RegisteredName) is string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when Explorer opens .pdf files with Leaf.</summary>
    public static bool IsDefaultPdfHandler()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice");
            return key?.GetValue("ProgId") is string progId && string.Equals(progId, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool PromptDismissed => File.Exists(DismissFlag);

    public static void DismissPrompt()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DismissFlag)!);
            File.WriteAllText(DismissFlag, DateTime.UtcNow.ToString("O"));
        }
        catch (IOException)
        {
            // best effort
        }
    }

    /// <summary>Opens Settings > Default apps > Leaf (has a one-click "Set default" button on Windows 11).</summary>
    public static async Task OpenDefaultAppsSettingsAsync()
    {
        string uri = IsRegisteredPerMachine()
            ? $"ms-settings:defaultapps?registeredAppMachine={RegisteredName}"
            : $"ms-settings:defaultapps?registeredAppUser={RegisteredName}";
        if (!await Launcher.LaunchUriAsync(new Uri(uri)))
        {
            await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
        }
    }
}
