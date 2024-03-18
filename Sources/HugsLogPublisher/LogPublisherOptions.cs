﻿using System;

namespace HugsLogPublisher;

internal interface ILogPublisherOptions
{
    bool UseCustomOptions { get; set; }
    bool IncludePlatformInfo { get; set; }
    bool AllowUnlimitedLogSize { get; set; }
    string AuthToken { get; set; }
}

[Serializable]
internal class LogPublisherOptions : IEquatable<LogPublisherOptions>, ILogPublisherOptions
{
    public bool UseCustomOptions { get; set; }

    public bool IncludePlatformInfo { get; set; }

    public bool AllowUnlimitedLogSize { get; set; }

    public string AuthToken { get; set; }

    public bool Equals(LogPublisherOptions other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return UseCustomOptions == other.UseCustomOptions
               && IncludePlatformInfo == other.IncludePlatformInfo
               && AllowUnlimitedLogSize == other.AllowUnlimitedLogSize
               && AuthToken == other.AuthToken;
    }
}
