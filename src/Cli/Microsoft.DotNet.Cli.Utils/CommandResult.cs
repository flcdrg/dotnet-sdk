﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Microsoft.DotNet.Cli.Utils
{
    public struct CommandResult
    {
        public static readonly CommandResult Empty = new();

        public ProcessStartInfo StartInfo { get; }
        public int ExitCode { get; }
        public string? StdOut { get; }
        public string? StdErr { get; }

        public CommandResult(ProcessStartInfo startInfo, int exitCode, string? stdOut, string? stdErr)
        {
            StartInfo = startInfo;
            ExitCode = exitCode;
            StdOut = stdOut;
            StdErr = stdErr;
        }
    }
}
