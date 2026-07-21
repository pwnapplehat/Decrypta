using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Decrypta.App.Services;
using Decrypta.Core;
using Decrypta.Core.Devices;
using Decrypta.Core.Diagnostics;
using Decrypta.Core.Tools;

namespace Decrypta.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Settings _settings = Settings.Load();
    private readonly DecryptaEngine _engine;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly StringBuilder _decryptLog = new();
    private readonly StringBuilder _signInLog = new();

    private RunningJob? _signInJob;
    private RunningJob? _decryptJob;
    private HashSet<string> _lastUdids = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel()
    {
        _engine = new DecryptaEngine(_settings);
        SshUser = _settings.SshUser;
        Storefront = _settings.Storefront;
        Verbose = _settings.VerboseLog;
        OutputDirectory = _settings.OutputDirectory;
        AutoUpdateCheck = _settings.AutoUpdateCheck;

        DecryptCommand = new RelayCommand(() => _ = RunDecryptAsync(), () => !IsBusy);
        SignInCommand = new RelayCommand(() => _ = RunSignInAsync(), () => !IsBusy);
        NewAccountCommand = new RelayCommand(NewAccount, () => !IsBusy);
        SwitchAccountCommand = new RelayCommand(SwitchAccount, () => !IsBusy);
        SignOutCommand = new RelayCommand(SignOutActive, () => !IsBusy);
        RemoveAccountCommand = new RelayCommand(RemoveActiveAccount, () => !IsBusy);
        SendConsoleCommand = new RelayCommand(SendConsole);
        RunDoctorCommand = new RelayCommand(() => _ = RunDoctorAsync(), () => !IsBusy);
        RefreshDevicesCommand = new RelayCommand(() => _ = RefreshDevicesAsync());
        RefreshLibraryCommand = new RelayCommand(RefreshLibrary);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        ChangeOutputFolderCommand = new RelayCommand(ChangeOutputFolder);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        CleanCacheCommand = new RelayCommand(() => _ = CleanCacheAsync(), () => !IsBusy);
        CancelCommand = new RelayCommand(CancelActive, () => IsBusy);
        InstallUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync(), () => !_updateInstalling);
        ViewUpdateCommand = new RelayCommand(ViewUpdate);
        DismissUpdateCommand = new RelayCommand(() => UpdateBannerVisible = false);

        RefreshAccounts();
        _ = RefreshCacheSizeAsync();
    }

    // ---- navigation ----
    /// <summary>Raised to ask the shell to switch tabs (e.g. jump to Sign in on a 2FA prompt).</summary>
    public event Action<int>? NavigateRequested;

    private void RequestNavigate(int tab) => _dispatcher.BeginInvoke(() => NavigateRequested?.Invoke(tab));

    // ---- device ----
    public ObservableCollection<DeviceInfo> Devices { get; } = [];

    private DeviceInfo? _selectedDevice;
    public DeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetProperty(ref _selectedDevice, value)) { Raise(nameof(HasDevice)); } }
    }

    public bool HasDevice => _selectedDevice is not null;

    private bool _serviceUp;
    public bool ServiceUp { get => _serviceUp; set => SetProperty(ref _serviceUp, value); }

    private string _deviceStatus = "scanning for devices…";
    public string DeviceStatus { get => _deviceStatus; set => SetProperty(ref _deviceStatus, value); }

    // ---- decrypt tab ----
    private string _decryptTarget = "";
    public string DecryptTarget { get => _decryptTarget; set => SetProperty(ref _decryptTarget, value); }

    private bool _sourceFromAppStore = true;
    public bool SourceFromAppStore { get => _sourceFromAppStore; set => SetProperty(ref _sourceFromAppStore, value); }

    private bool _skipAppex;
    public bool SkipAppex { get => _skipAppex; set => SetProperty(ref _skipAppex, value); }

    private bool _patchDeviceType;
    public bool PatchDeviceType { get => _patchDeviceType; set => SetProperty(ref _patchDeviceType, value); }

    private bool _verbose = true;
    public bool Verbose { get => _verbose; set => SetProperty(ref _verbose, value); }

    private string _extVersionId = "";
    public string ExtVersionId { get => _extVersionId; set => SetProperty(ref _extVersionId, value); }

    private string _storefront = "";
    public string Storefront { get => _storefront; set => SetProperty(ref _storefront, value); }

    private string _decryptLogText = "";
    public string DecryptLogText { get => _decryptLogText; set => SetProperty(ref _decryptLogText, value); }

    // ---- sign-in tab ----
    private string _email = "";
    public string Email { get => _email; set => SetProperty(ref _email, value); }

    public string ApplePassword { get; set; } = "";
    public string SshPassword { get; set; } = "";

    private string _sshUser = "root";
    public string SshUser { get => _sshUser; set => SetProperty(ref _sshUser, value); }

    private string _signInLogText = "";
    public string SignInLogText { get => _signInLogText; set => SetProperty(ref _signInLogText, value); }

    private string _promptHint = "response";
    public string PromptHint { get => _promptHint; set => SetProperty(ref _promptHint, value); }

    private string _consoleInput = "";
    public string ConsoleInput { get => _consoleInput; set => SetProperty(ref _consoleInput, value); }

    // ---- doctor tab ----
    public ObservableCollection<Check> DoctorRows { get; } = [];

    // ---- library tab ----
    public ObservableCollection<OutputFileVm> OutputFiles { get; } = [];

    // ---- settings tab ----
    private string _outputDirectory = "";
    public string OutputDirectory { get => _outputDirectory; set => SetProperty(ref _outputDirectory, value); }

    private bool _autoUpdateCheck = true;
    public bool AutoUpdateCheck { get => _autoUpdateCheck; set => SetProperty(ref _autoUpdateCheck, value); }

    // ---- status bar: signed-in summary + indicator ----
    private string _signedInSummary = "not signed in";
    public string SignedInSummary { get => _signedInSummary; set => SetProperty(ref _signedInSummary, value); }

    private bool _isSignedIn;
    public bool IsSignedIn { get => _isSignedIn; set => SetProperty(ref _isSignedIn, value); }

    // ---- accounts (multiple Apple IDs) ----
    public ObservableCollection<AccountView> Accounts { get; } = [];

    private AccountView? _selectedAccount;
    public AccountView? SelectedAccount { get => _selectedAccount; set => SetProperty(ref _selectedAccount, value); }

    public bool HasAccounts => Accounts.Count > 0;

    // ---- cache / storage ----
    private string _cacheSizeText = "—";
    public string CacheSizeText { get => _cacheSizeText; set => SetProperty(ref _cacheSizeText, value); }

    // ---- auto-update banner ----
    private UpdateInfo? _pendingUpdate;
    private bool _updateInstalling;

    private bool _updateBannerVisible;
    public bool UpdateBannerVisible { get => _updateBannerVisible; set => SetProperty(ref _updateBannerVisible, value); }

    private string _updateBannerText = "";
    public string UpdateBannerText { get => _updateBannerText; set => SetProperty(ref _updateBannerText, value); }

    private bool _updateCanAutoInstall;
    public bool UpdateCanAutoInstall { get => _updateCanAutoInstall; set => SetProperty(ref _updateCanAutoInstall, value); }

    public RelayCommand InstallUpdateCommand { get; }
    public RelayCommand ViewUpdateCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }

    // ---- global ----
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                Raise(nameof(ProgressActive));
                DecryptCommand.RaiseCanExecuteChanged();
                SignInCommand.RaiseCanExecuteChanged();
                RunDoctorCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ProgressActive => _isBusy;

    private string _status = "ready";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public RelayCommand DecryptCommand { get; }
    public RelayCommand SignInCommand { get; }
    public RelayCommand NewAccountCommand { get; }
    public RelayCommand SwitchAccountCommand { get; }
    public RelayCommand SignOutCommand { get; }
    public RelayCommand RemoveAccountCommand { get; }
    public RelayCommand SendConsoleCommand { get; }
    public RelayCommand RunDoctorCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand RefreshLibraryCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand ChangeOutputFolderCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand CleanCacheCommand { get; }
    public RelayCommand CancelCommand { get; }

    // ===================================================================
    //  Device discovery
    // ===================================================================

    public void StartDevicePolling()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => _ = PollDevicesAsync();
        timer.Start();
        _ = RefreshDevicesAsync();
        RefreshLibrary();
        if (AutoUpdateCheck)
        {
            _ = CheckForUpdatesAsync();
        }
    }

    // ===================================================================
    //  Auto-update (GitHub releases)
    // ===================================================================

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                return;
            }
            _pendingUpdate = info;
            UpdateCanAutoInstall = info.InstallerUrl.Length > 0 && info.ChecksumsUrl.Length > 0;
            UpdateBannerText = $"Decrypta {info.Version.ToString(3)} is available"
                + (UpdateCanAutoInstall ? "." : " — open the release page to download.");
            UpdateBannerVisible = true;
        }
        catch (Exception)
        {
            // Update check is best-effort and must never disrupt the app.
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null || _updateInstalling)
        {
            return;
        }
        if (!UpdateCanAutoInstall)
        {
            ViewUpdate();
            return;
        }
        _updateInstalling = true;
        InstallUpdateCommand.RaiseCanExecuteChanged();
        Status = "downloading update…";
        try
        {
            var progress = new Progress<double>(p => Status = $"downloading update… {p * 100:0}%");
            string installer = await UpdateService.DownloadVerifiedInstallerAsync(_pendingUpdate, progress);
            Status = "launching installer…";
            UpdateService.LaunchInstaller(installer);
            // Quit so the installer can replace the files and relaunch.
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Status = $"update failed: {ex.Message}";
            _updateInstalling = false;
            InstallUpdateCommand.RaiseCanExecuteChanged();
        }
    }

    private void ViewUpdate()
    {
        if (_pendingUpdate is not null)
        {
            UpdateService.OpenReleasePage(_pendingUpdate.ReleasePageUrl);
        }
    }

    private async Task PollDevicesAsync()
    {
        try
        {
            var quick = await Task.Run(() => _engine.Devices.QuickList());
            var udids = quick.Select(q => q.Udid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ServiceUp = quick.Count > 0;
            if (!udids.SetEquals(_lastUdids))
            {
                _lastUdids = udids;
                await RefreshDevicesAsync();
            }
            else if (quick.Count == 0)
            {
                DeviceStatus = "no device connected";
            }
        }
        catch (Exception)
        {
            ServiceUp = false;
        }
    }

    private async Task RefreshDevicesAsync()
    {
        List<DeviceInfo> found;
        try
        {
            found = await Task.Run(() => _engine.Devices.ServiceReachable()
                ? _engine.Devices.ListDevices()
                : []);
        }
        catch (Exception)
        {
            found = [];
        }

        var previous = SelectedDevice?.Udid;
        Devices.Clear();
        foreach (var d in found)
        {
            Devices.Add(d);
        }
        _lastUdids = found.Select(d => d.Udid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ServiceUp = found.Count > 0;

        if (found.Count == 0)
        {
            SelectedDevice = null;
            DeviceStatus = "no device connected";
            return;
        }

        SelectedDevice = found.FirstOrDefault(d => d.Udid == previous) ?? found[0];
        DeviceStatus = $"connected · {SelectedDevice!.ConnectionSummary}";
    }

    // ===================================================================
    //  Sign in
    // ===================================================================

    private async Task RunSignInAsync()
    {
        if (IsBusy)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(ApplePassword))
        {
            Status = "enter your Apple ID email and password";
            return;
        }
        if (string.IsNullOrEmpty(SshPassword))
        {
            Status = "enter the device SSH password (palera1n default: alpine)";
            return;
        }
        if (SelectedDevice is null)
        {
            Status = "no device selected";
            return;
        }

        _signInLog.Clear();
        SignInLogText = "";
        PromptHint = "response";
        IsBusy = true;
        Status = "signing in — watch for a 2FA prompt below";
        _settings.SshUser = SshUser;
        try
        {
            _signInJob = _engine.StartSignIn(SelectedDevice, Email.Trim(), ApplePassword,
                string.IsNullOrWhiteSpace(SshUser) ? "root" : SshUser.Trim(), SshPassword, AppendSignIn);
            int rc = await _signInJob.Completion;
            Status = rc == 0 ? "sign-in complete" : $"sign-in exited with code {rc}";
            AppendSignIn($"\n[exit {rc}]\n");
            RefreshAccounts();
        }
        catch (DecryptaException ex)
        {
            AppendSignIn($"\nerror: {ex.Message}\n");
            Status = ex.Message;
        }
        finally
        {
            _signInJob = null;
            IsBusy = false;
        }
    }

    private void SendConsole()
    {
        var job = _signInJob;
        if (job is { IsRunning: true })
        {
            job.SendLine(ConsoleInput);
            AppendSignIn($"\n> {(string.IsNullOrEmpty(ConsoleInput) ? "(enter)" : ConsoleInput)}\n");
            ConsoleInput = "";
            PromptHint = "response";
        }
        else
        {
            Status = "nothing is waiting for input right now";
        }
    }

    // ===================================================================
    //  Accounts (multiple Apple IDs)
    // ===================================================================

    private void RefreshAccounts()
    {
        var list = _engine.ListAccounts();
        Accounts.Clear();
        foreach (var a in list)
        {
            Accounts.Add(a);
        }
        SelectedAccount = list.FirstOrDefault(a => a.IsActive) ?? list.FirstOrDefault();
        Raise(nameof(HasAccounts));

        IsSignedIn = _engine.IsSignedIn;
        var email = _engine.SignedInEmail;
        SignedInSummary = IsSignedIn && email is not null ? email : "not signed in";
        // If the active account has creds, prefill the email box for clarity.
        if (email is not null && string.IsNullOrWhiteSpace(Email))
        {
            Email = email;
        }
    }

    /// <summary>Start adding a new Apple ID: clear the form and focus sign-in.</summary>
    private void NewAccount()
    {
        Email = "";
        ApplePassword = "";
        SshPassword = "";
        ClearPasswordBoxesRequested?.Invoke();
        _signInLog.Clear();
        SignInLogText = "";
        Status = "enter a new Apple ID and press Sign in";
        RequestNavigate(1);
    }

    /// <summary>Raised so the Sign in view can clear its PasswordBoxes (they can't be bound).</summary>
    public event Action? ClearPasswordBoxesRequested;

    private void SwitchAccount()
    {
        if (SelectedAccount is null)
        {
            return;
        }
        _engine.SwitchAccount(SelectedAccount.Slug);
        RefreshAccounts();
        Status = $"switched to {SelectedAccount?.Email}";
    }

    private void RemoveActiveAccount()
    {
        var acc = SelectedAccount;
        if (acc is null)
        {
            return;
        }
        _engine.RemoveAccount(acc.Slug);
        AppendSignIn($"\n[removed account {acc.Email}]\n");
        Status = $"removed {acc.Email}";
        RefreshAccounts();
    }

    private void SignOutActive()
    {
        _engine.SignOutActive();
        AppendSignIn("\n[signed out - cleared stored Apple credentials for the active account]\n");
        Status = "signed out";
        RefreshAccounts();
    }

    // ===================================================================
    //  Cache / storage
    // ===================================================================

    private async Task RefreshCacheSizeAsync()
    {
        try
        {
            long bytes = await Task.Run(() => _engine.CacheSizeBytes());
            CacheSizeText = bytes <= 0 ? "empty" : HumanSize(bytes);
        }
        catch (Exception)
        {
            CacheSizeText = "—";
        }
    }

    private async Task CleanCacheAsync()
    {
        if (IsBusy)
        {
            Status = "can't clean while a job is running";
            return;
        }
        Status = "cleaning cache…";
        long freed = await Task.Run(() => _engine.CleanCache());
        await RefreshCacheSizeAsync();
        Status = freed > 0 ? $"cleaned {HumanSize(freed)} of cached/partial downloads" : "cache already empty";
    }

    private void ChangeOutputFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where decrypted IPAs (and their download cache) are saved",
            InitialDirectory = Directory.Exists(OutputDirectory) ? OutputDirectory : AppPaths.DefaultOutputDir,
        };
        if (dialog.ShowDialog() == true)
        {
            OutputDirectory = dialog.FolderName;
            SaveSettings();
            _ = RefreshCacheSizeAsync();
        }
    }

    private void AppendSignIn(string text)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _signInLog.Append(text);
            TrimBuilder(_signInLog);
            SignInLogText = _signInLog.ToString();
            var low = text.ToLowerInvariant();
            if (low.Contains("enter it") || low.Contains("6-digit"))
            {
                PromptHint = "enter the 6-digit code";
                RequestNavigate(1); // Sign in tab
            }
            else if (low.Contains("press enter"))
            {
                PromptHint = "install the prereq, then Send";
            }
        });
    }

    // ===================================================================
    //  Decrypt
    // ===================================================================

    private async Task RunDecryptAsync()
    {
        if (IsBusy)
        {
            return;
        }
        var target = DecryptTarget.Trim();
        if (string.IsNullOrEmpty(target))
        {
            Status = "enter an app (bundle id / id / url / .ipa)";
            return;
        }
        if (SelectedDevice is null)
        {
            Status = "no device selected";
            return;
        }

        _decryptLog.Clear();
        DecryptLogText = "";
        IsBusy = true;

        // "Use installed build" only works when ipadecrypt gets a bundle id (it matches the
        // installed app by bundle id). If the user pasted an App Store link/id, resolve it to
        // the bundle id first via Apple's public lookup - otherwise ipadecrypt would fall back
        // to downloading from the App Store, ignoring the toggle.
        if (!SourceFromAppStore && !DecryptaEngine.IsLocalIpa(target) &&
            !Decrypta.Core.AppStore.AppStoreLookup.LooksLikeBundleId(target))
        {
            var (appId, country) = Decrypta.Core.AppStore.AppStoreLookup.ParseAppStoreRef(target);
            if (appId is not null)
            {
                Status = $"resolving App Store id {appId}…";
                var countries = new[] { country, string.IsNullOrWhiteSpace(Storefront) ? null : Storefront.Trim() };
                var bundleId = await Decrypta.Core.AppStore.AppStoreLookup.LookupBundleIdAsync(appId, countries);
                if (bundleId is not null)
                {
                    AppendDecrypt($"[resolve] App Store id {appId} -> {bundleId} (using installed build)\n");
                    target = bundleId;
                }
                else
                {
                    AppendDecrypt($"[resolve] couldn't resolve id {appId} to a bundle id — it will be fetched from the App Store instead. Tip: paste the bundle id directly to use the installed build.\n");
                }
            }
        }

        var flags = new List<string>();
        if (Verbose)
        {
            flags.Add("--verbose");
        }
        flags.Add(SourceFromAppStore ? "--from-appstore" : "--use-installed");
        if (SkipAppex)
        {
            flags.Add("--skip-appex");
        }
        if (PatchDeviceType)
        {
            flags.Add("--patch-device-type");
        }
        if (!string.IsNullOrWhiteSpace(ExtVersionId))
        {
            flags.Add("--external-version-id");
            flags.Add(ExtVersionId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(Storefront))
        {
            flags.Add("--storefront");
            flags.Add(Storefront.Trim());
        }

        string? output = DecryptaEngine.IsLocalIpa(target)
            ? null
            : DecryptaEngine.DefaultOutputPath(OutputDirectory, target);

        Status = $"decrypting {target}…";
        try
        {
            _decryptJob = _engine.StartDecrypt(SelectedDevice, target, output, flags, AppendDecrypt);
            int rc = await _decryptJob.Completion;
            AppendDecrypt($"\n[exit {rc}]\n");
            Status = rc == 0 ? "decrypt complete" : $"decrypt failed (exit {rc})";
            if (rc == 0)
            {
                RefreshLibrary();
            }
            else
            {
                // An interrupted/failed decrypt can leave a partial .tmp download — clear it.
                long freed = await Task.Run(() => _engine.CleanPartials());
                if (freed > 0)
                {
                    AppendDecrypt($"[cache] removed {HumanSize(freed)} of partial download\n");
                }
            }
            await RefreshCacheSizeAsync();
        }
        catch (DecryptaException ex)
        {
            AppendDecrypt($"\nerror: {ex.Message}\n");
            Status = ex.Message;
        }
        finally
        {
            _decryptJob = null;
            IsBusy = false;
        }
    }

    private void AppendDecrypt(string text)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _decryptLog.Append(text);
            TrimBuilder(_decryptLog);
            DecryptLogText = _decryptLog.ToString();
        });
    }

    private void CancelActive()
    {
        _decryptJob?.Cancel();
        _signInJob?.Cancel();
        Status = "cancelling…";
    }

    // ===================================================================
    //  Doctor
    // ===================================================================

    private async Task RunDoctorAsync()
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        Status = "running checks…";
        DoctorRows.Clear();
        try
        {
            var udid = SelectedDevice?.Udid;
            var checks = await Task.Run(() => new Doctor().Run(udid));
            foreach (var c in checks)
            {
                DoctorRows.Add(c);
            }
            Status = "doctor complete";
        }
        catch (Exception ex)
        {
            Status = $"doctor error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ===================================================================
    //  Library / output
    // ===================================================================

    public void RefreshLibrary()
    {
        OutputFiles.Clear();
        try
        {
            if (!Directory.Exists(OutputDirectory))
            {
                return;
            }
            var files = new DirectoryInfo(OutputDirectory)
                .GetFiles("*.ipa")
                .OrderByDescending(f => f.LastWriteTimeUtc);
            foreach (var f in files)
            {
                OutputFiles.Add(new OutputFileVm(f.Name, HumanSize(f.Length), f.FullName));
            }
        }
        catch (IOException)
        {
            // ignore transient IO
        }
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"cannot open folder: {ex.Message}";
        }
    }

    public void RevealFile(string fullPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"cannot reveal: {ex.Message}";
        }
    }

    // ===================================================================
    //  Settings
    // ===================================================================

    private void SaveSettings()
    {
        _settings.OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory)
            ? AppPaths.DefaultOutputDir : OutputDirectory;
        _settings.SshUser = string.IsNullOrWhiteSpace(SshUser) ? "root" : SshUser;
        _settings.Storefront = Storefront;
        _settings.VerboseLog = Verbose;
        _settings.AutoUpdateCheck = AutoUpdateCheck;
        _settings.Save();
        OutputDirectory = _settings.OutputDirectory;
        RefreshLibrary();
        Status = "settings saved";
    }

    // ===================================================================
    //  Helpers
    // ===================================================================

    private static void TrimBuilder(StringBuilder sb, int max = 100_000)
    {
        if (sb.Length > max)
        {
            sb.Remove(0, sb.Length - max);
        }
    }

    private static string HumanSize(long n)
    {
        if (n < 1024)
        {
            return $"{n} B";
        }
        double d = n;
        foreach (var unit in new[] { "KB", "MB", "GB" })
        {
            d /= 1024;
            if (d < 1024 || unit == "GB")
            {
                return $"{d:0.0} {unit}";
            }
        }
        return $"{d:0.0} GB";
    }
}
