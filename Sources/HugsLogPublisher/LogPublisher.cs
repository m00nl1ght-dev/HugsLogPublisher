using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using Verse;

namespace HugsLogPublisher;

/// <summary>
/// Collects the game logs and loaded mods and posts the information on GitHub as a gist.
/// </summary>
public class LogPublisher
{
    public static readonly LogPublisher Instance = new();

    public enum PublisherStatus
    {
        Ready,
        Uploading,
        Done,
        Error
    }

    private const string RequestUserAgent = "HugsLib_log_uploader";
    private const string OutputLogFilename = "output_log.txt";

    private const string GistApiUrl = "https://api.github.com/gists";

    private const string GistPayloadJson =
        "{{\"description\":\"{0}\",\"public\":{1},\"files\":{{\"{2}\":{{\"content\":\"{3}\"}}}}}}";

    private const string GistDescription = "Rimworld output log published using HugsLib Standalone Log Publisher";

    private const string AlternativeApiUrl = "https://api.paste.gg/v1/pastes";
    private const string AlternativeSubPath = "rimworld-game-logs";

    private const string AlternativePayloadJson =
        "{{\"name\": \"{0}\", \"visibility\": \"unlisted\", \"expires\": \"{1}\", " +
        "\"files\": [{{\"name\": \"{2}\", \"content\": {{\"format\": \"text\", \"value\": \"{3}\"}}}}]}}";

    private const int MaxLogLineCount = 10000;
    private const float PublishRequestTimeout = 90f;

    private readonly string _gitHubAuthToken =
        "RuEvo2u9gsaCeKA9Bamh4sa57FOikUYkHhLH_phg".Reverse().Join(""); // GitHub will revoke any tokens committed

    private readonly string _alternativeAuthToken =
        "c901ee6c586731e9ad44a9345426ffb2".Reverse().Join("");

    private readonly Regex _uploadResponseUrlMatch = new Regex("\"html_url\":\"(https://gist\\.github\\.com/[\\w/]+)\"");
    private readonly Regex _alternativeResponseUrlMatch = new Regex("\"id\":\"([\\w/]+)\"");

    private UnityWebRequest _activeRequest;
    private Thread _mockThread;

    private LogPublisherOptions _publishOptions = new();
    private bool _userAborted;

    public PublisherStatus Status { get; private set; }
    public string ErrorMessage { get; private set; }
    public string ResultUrl { get; private set; }

    public void ShowPublishPrompt()
    {
        if (PublisherIsReady())
        {
            Find.WindowStack.Add(new Dialog_PublishLogsOptions(
                "HugsLogPublisher.shareConfirmTitle".Translate(),
                "HugsLogPublisher.shareConfirmMessage".Translate(),
                _publishOptions
            )
            {
                OnUpload = OnPublishConfirmed,
                OnCopy = CopyToClipboard
            });
        }
        else
        {
            ShowPublishDialog();
        }
    }

    public void AbortUpload()
    {
        if (Status != PublisherStatus.Uploading) return;
        _userAborted = true;

        if (_activeRequest is { isDone: false })
        {
            _activeRequest.Abort();
        }

        _activeRequest = null;

        if (_mockThread is { IsAlive: true })
        {
            _mockThread.Interrupt();
        }

        ErrorMessage = "Aborted by user";
        FinalizeUpload(false);
    }

    public void BeginUpload()
    {
        if (!PublisherIsReady()) return;

        Status = PublisherStatus.Uploading;
        ErrorMessage = null;
        _userAborted = false;

        var collatedData = PrepareLogData();

        if (collatedData == null)
        {
            ErrorMessage = "Failed to collect data";
            FinalizeUpload(false);
            return;
        }

        void OnRequestFailed(Exception ex)
        {
            if (_userAborted) return;
            OnRequestError(ex.Message);
            Log.Warning("Exception during log publishing (gist creation): " + ex);
        }

        try
        {
            collatedData = CleanForJson(collatedData);

            if (_publishOptions.UseAlternativePlatform)
            {
                var expiry = DateTime.UtcNow.AddDays(7).ToString("o", CultureInfo.InvariantCulture);
                var payload = string.Format(AlternativePayloadJson,
                    GistDescription, expiry, OutputLogFilename, collatedData);

                _activeRequest = new UnityWebRequest(AlternativeApiUrl, UnityWebRequest.kHttpVerbPOST);
                _activeRequest.SetRequestHeader("Authorization", "Key " + _alternativeAuthToken);
                _activeRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload)) { contentType = "application/json" };
                _activeRequest.downloadHandler = new DownloadHandlerBuffer();
            }
            else
            {
                var useCustomAuthToken = !string.IsNullOrWhiteSpace(_publishOptions.AuthToken);
                var authToken = useCustomAuthToken
                    ? _publishOptions.AuthToken.Trim()
                    : _gitHubAuthToken;
                var publicVisibility = useCustomAuthToken ? "false" : "true";
                var payload = string.Format(GistPayloadJson,
                    GistDescription, publicVisibility, OutputLogFilename, collatedData);

                _activeRequest = new UnityWebRequest(GistApiUrl, UnityWebRequest.kHttpVerbPOST);
                _activeRequest.SetRequestHeader("Authorization", "token " + authToken);
                _activeRequest.SetRequestHeader("User-Agent", RequestUserAgent);
                _activeRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload)) { contentType = "application/json" };
                _activeRequest.downloadHandler = new DownloadHandlerBuffer();
            }

            HugsLibUtility.AwaitUnityWebResponse(_activeRequest, OnUploadComplete, OnRequestFailed,
                HttpStatusCode.Created, PublishRequestTimeout);
        }
        catch (Exception e)
        {
            OnRequestFailed(e);
        }
    }

    public void CopyToClipboard()
    {
        HugsLibUtility.CopyToClipboard(PrepareLogData());
    }

    public bool ShouldSuggestAlternativePlatform =>
        ErrorMessage != null && ErrorMessage.Contains("HTTP/1.1") && !_publishOptions.UseAlternativePlatform;

    public void UseAlternativePlatformAfterError()
    {
        _publishOptions.UseCustomOptions = true;
        _publishOptions.UseAlternativePlatform = true;
        _publishOptions.AllowUnlimitedLogSize = false;
    }

    private void OnPublishConfirmed()
    {
        if (!_publishOptions.UseCustomOptions) _publishOptions = new LogPublisherOptions();

        BeginUpload();
        ShowPublishDialog();
    }

    private void ShowPublishDialog()
    {
        Find.WindowStack.Add(new Dialog_PublishLogs(this));
    }

    private void OnRequestError(string errorMessage)
    {
        ErrorMessage = errorMessage;
        FinalizeUpload(false);
    }

    private void OnUploadComplete(string response)
    {
        var matchedUrl = TryExtractGistUrlFromUploadResponse(response);
        if (matchedUrl == null)
        {
            OnRequestError("Failed to parse response");
            return;
        }

        ResultUrl = matchedUrl;
        FinalizeUpload(true);
    }

    private void FinalizeUpload(bool success)
    {
        Status = success ? PublisherStatus.Done : PublisherStatus.Error;
        _activeRequest = null;
        _mockThread = null;
    }

    private string TryExtractGistUrlFromUploadResponse(string response)
    {
        if (_publishOptions.UseAlternativePlatform)
        {
            var match = _alternativeResponseUrlMatch.Match(response);
            if (!match.Success) return null;
            return $"https://paste.gg/p/{AlternativeSubPath}/{match.Groups[1]}";
        }
        else
        {
            var match = _uploadResponseUrlMatch.Match(response);
            if (!match.Success) return null;
            return match.Groups[1].ToString();
        }
    }

    private bool PublisherIsReady()
    {
        return Status is PublisherStatus.Ready or PublisherStatus.Done or PublisherStatus.Error;
    }

    private string PrepareLogData()
    {
        try
        {
            var logSection = GetLogFileContents();
            logSection = NormalizeLineEndings(logSection);
            // redact logs for privacy
            logSection = RedactRimworldPaths(logSection);
            logSection = RedactPlayerConnectInformation(logSection);
            logSection = RedactRendererInformation(logSection);
            logSection = RedactHomeDirectoryPaths(logSection);
            logSection = RedactSteamId(logSection);
            logSection = RedactUselessLines(logSection);
            logSection = ConsolidateRepeatedLines(logSection);
            logSection = TrimExcessLines(logSection);
            var collatedData = string.Concat(MakeLogTimestamp(),
                ListActiveMods(), "\n",
                ListHarmonyPatches(), "\n",
                ListPlatformInfo(), "\n",
                logSection);
            return collatedData;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }

        return null;
    }

    private string NormalizeLineEndings(string log)
    {
        return log.Replace("\r\n", "\n");
    }

    private string TrimExcessLines(string log)
    {
        if (_publishOptions.AllowUnlimitedLogSize && !_publishOptions.UseAlternativePlatform) return log;
        var indexOfLastNewline = IndexOfOccurence(log, '\n', MaxLogLineCount);
        if (indexOfLastNewline >= 0)
        {
            log =
                $"{log.Substring(0, indexOfLastNewline + 1)}(log trimmed to {MaxLogLineCount:N0} lines. Use publishing options to upload the full log)";
        }

        return log;
    }

    private int IndexOfOccurence(string s, char match, int occurence)
    {
        int currentOccurence = 1;
        int currentIndex = 0;
        while (currentOccurence <= occurence && (currentIndex = s.IndexOf(match, currentIndex + 1)) != -1)
        {
            if (currentOccurence == occurence) return currentIndex;
            currentOccurence++;
        }

        return -1;
    }

    private string RedactUselessLines(string log)
    {
        log = Regex.Replace(log, "Non platform assembly:.+\n", "");
        log = Regex.Replace(log, "Platform assembly: .+\n", "");
        log = Regex.Replace(log, "Fallback handler could not load library.+\n", "");
        log = Regex.Replace(log, "- Completed reload, in [\\d\\. ]+ seconds\n", "");
        log = Regex.Replace(log, "UnloadTime: [\\d\\. ]+ ms\n", "");
        log = Regex.Replace(log, "<RI> Initializing input\\.\r\n", "");
        log = Regex.Replace(log, "<RI> Input initialized\\.\r\n", "");
        log = Regex.Replace(log, "<RI> Initialized touch support\\.\r\n", "");
        log = Regex.Replace(log, "\\(Filename: .+Line: .+\\)\n", "");
        log = Regex.Replace(log, "\n \n", "\n");
        return log;
    }

    private string RedactSteamId(string log)
    {
        const string idReplacement = "[Steam Id redacted]";
        return Regex.Replace(log, ".+SetMinidumpSteamID.+", idReplacement);
    }

    private string RedactHomeDirectoryPaths(string log)
    {
        const string pathReplacement = "[Home_dir]";
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Regex.Replace(log, Regex.Escape(homePath), pathReplacement, RegexOptions.IgnoreCase);
    }


    private string RedactRimworldPaths(string log)
    {
        const string pathReplacement = "[Rimworld_dir]";
        // easiest way to get the game folder is one level up from dataPath
        var appPath = Path.GetFullPath(Application.dataPath);
        var pathParts = appPath.Split(Path.DirectorySeparatorChar).ToList();
        pathParts.RemoveAt(pathParts.Count - 1);
        appPath = pathParts.Join(Path.DirectorySeparatorChar.ToString());
        log = log.Replace(appPath, pathReplacement);
        if (Path.DirectorySeparatorChar != '/')
        {
            // log will contain mixed windows and unix style paths
            appPath = appPath.Replace(Path.DirectorySeparatorChar, '/');
            log = log.Replace(appPath, pathReplacement);
        }

        return log;
    }

    private string RedactRendererInformation(string log)
    {
        if (_publishOptions.IncludePlatformInfo) return log;
        // apparently renderer information can appear multiple times in the log
        for (int i = 0; i < 5; i++)
        {
            var redacted = RedactString(log, "GfxDevice: ", "\nBegin MonoManager", "[Renderer information redacted]");
            if (log.Length == redacted.Length) break;
            log = redacted;
        }

        return log;
    }

    private string RedactPlayerConnectInformation(string log)
    {
        return RedactString(log, "PlayerConnection ", "Initialize engine", "[PlayerConnect information redacted]\n");
    }

    private string GetLogFileContents()
    {
        var filePath = HugsLibUtility.TryGetLogFilePath(_publishOptions.UsePreviousLog);
        if (filePath.NullOrEmpty() || !File.Exists(filePath))
        {
            throw new FileNotFoundException($"Log file not found: {filePath}");
        }

        var tempPath = Path.GetTempFileName();
        File.Delete(tempPath);
        // we need to copy the log file since the original is already opened for writing by Unity
        File.Copy(filePath, tempPath);
        var fileContents = File.ReadAllText(tempPath);
        File.Delete(tempPath);
        return (_publishOptions.UsePreviousLog ? "Log file contents from previous game launch:\n" : "Log file contents:\n") + fileContents;
    }

    private string MakeLogTimestamp()
    {
        return string.Concat("Log uploaded on ", DateTime.Now.ToLongDateString(), ", ", DateTime.Now.ToLongTimeString(),
            "\n");
    }

    private string RedactString(string original, string redactStart, string redactEnd, string replacement)
    {
        var startIndex = original.IndexOf(redactStart, StringComparison.Ordinal);
        var endIndex = original.IndexOf(redactEnd, StringComparison.Ordinal);
        string result = original;
        if (startIndex >= 0 && endIndex >= 0)
        {
            var logTail = original.Substring(endIndex);
            result = original.Substring(0, startIndex + redactStart.Length);
            result += replacement;
            result += logTail;
        }

        return result;
    }

    private string ListHarmonyPatches()
    {
        var patchListing = HarmonyUtility.DescribeAllPatchedMethods();

        return string.Concat("Active Harmony patches:\n",
            patchListing,
            patchListing.EndsWith("\n") ? "" : "\n",
            HarmonyUtility.DescribeHarmonyVersions(), "\n");
    }

    private string ListPlatformInfo()
    {
        const string sectionTitle = "Platform information: ";
        if (_publishOptions.IncludePlatformInfo)
        {
            return string.Concat(sectionTitle, "\nCPU: ",
                SystemInfo.processorType,
                "\nOS: ",
                SystemInfo.operatingSystem,
                "\nMemory: ",
                SystemInfo.systemMemorySize,
                " MB",
                "\n");
        }

        return sectionTitle + "(hidden, use publishing options to include)\n";
    }

    private string ListActiveMods()
    {
        var builder = new StringBuilder();
        builder.Append("Loaded mods:\n");
        foreach (var modContentPack in LoadedModManager.RunningMods)
        {
            builder.AppendFormat("{0}({1})", modContentPack.Name, modContentPack.PackageIdPlayerFacing);
            #if RW_1_5_OR_GREATER
            TryAppendModMetaVersion(builder, modContentPack);
            #endif
            TryAppendOverrideVersion(builder, modContentPack);
            TryAppendManifestVersion(builder, modContentPack);
            builder.Append(": ");
            var firstAssembly = true;
            var anyAssemblies = false;
            foreach (var loadedAssembly in modContentPack.assemblies.loadedAssemblies)
            {
                if (!firstAssembly)
                {
                    builder.Append(", ");
                }

                firstAssembly = false;
                builder.Append(loadedAssembly.GetName().Name);
                builder.AppendFormat("({0})", AssemblyVersionInfo.ReadModAssembly(loadedAssembly, modContentPack));
                anyAssemblies = true;
            }

            if (!anyAssemblies)
            {
                builder.Append("(no assemblies)");
            }

            builder.Append("\n");
        }

        return builder.ToString();
    }

    #if RW_1_5_OR_GREATER

    private static void TryAppendModMetaVersion(StringBuilder builder, ModContentPack modContentPack)
    {
        if (!string.IsNullOrEmpty(modContentPack.ModMetaData.ModVersion))
        {
            builder.AppendFormat("[v:{0}]", modContentPack.ModMetaData.ModVersion);
        }
    }

    #endif

    private static void TryAppendOverrideVersion(StringBuilder builder, ModContentPack modContentPack)
    {
        var versionFile = VersionFile.TryParseVersionFile(modContentPack);
        if (versionFile != null && versionFile.OverrideVersion != null)
        {
            builder.AppendFormat("[ov:{0}]", versionFile.OverrideVersion);
        }
    }

    private static void TryAppendManifestVersion(StringBuilder builder, ModContentPack modContentPack)
    {
        var manifestFile = ManifestFile.TryParse(modContentPack);
        if (manifestFile != null && manifestFile.Version != null)
        {
            builder.AppendFormat("[mv:{0}]", manifestFile.Version);
        }
    }

    // sanitizes a string for valid inclusion in JSON
    private static string CleanForJson(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        int i;
        int len = s.Length;
        var sb = new StringBuilder(len + 4);
        for (i = 0; i < len; i += 1)
        {
            var c = s[i];
            switch (c)
            {
                case '\\':
                case '"':
                    sb.Append('\\');
                    sb.Append(c);
                    break;
                case '/':
                    sb.Append('\\');
                    sb.Append(c);
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                default:
                    if (c < ' ')
                    {
                        var t = "000" + "X";
                        sb.Append("\\u" + t.Substring(t.Length - 4));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    internal static string ConsolidateRepeatedLines(string log)
    {
        const int searchRange = 40;
        const int minRepetitions = 2;
        const int minLength = 25;

        try
        {
            var lines = log.Split('\n');

            var result = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                result.Append(line).Append('\n');

                for (int o = 1; o < searchRange && i + 2 * o <= lines.Length; o++)
                {
                    bool match;

                    int r = 0;
                    int j = i;

                    do
                    {
                        match = lines[j] == lines[j + o];

                        if (match)
                        {
                            for (int k = 1; k < o; k++)
                            {
                                if (lines[j + k] != lines[j + o + k])
                                {
                                    match = false;
                                    break;
                                }
                            }

                            if (match)
                            {
                                j += o;
                                r++;
                            }
                        }
                    }
                    while (match && j + 2 * o <= lines.Length);

                    if (r >= minRepetitions && (r + 1) * o >= minLength)
                    {
                        for (int k = 1; k < o - 1; k++)
                        {
                            result.Append(lines[i + k]).Append('\n');
                        }

                        var n = lines[i].Length == 0 ? o - 1 : o;

                        if (lines[i + o - 1].Length != 0)
                        {
                            result.Append(lines[i + o - 1]).Append('\n');
                        }
                        else
                        {
                            n--;
                        }

                        if (n == 1)
                            result.Append($"########## The preceding line was repeated {r} times ##########").Append('\n');
                        else if (n > 1)
                            result.Append($"########## The preceding {n} lines were repeated {r} times ##########").Append('\n');

                        i += o * r + (o - 1);

                        if (n >= 1 && i + 1 < lines.Length && lines[i + 1].Length != 0)
                        {
                            result.Append('\n');
                        }

                        break;
                    }
                }
            }

            return result.ToString();
        }
        catch (Exception ex)
        {
            Log.Warning("Exception while consolidating repeated log lines: " + ex);
            return log + "\n[Failed to consolidate repeated log lines]";
        }
    }
}
