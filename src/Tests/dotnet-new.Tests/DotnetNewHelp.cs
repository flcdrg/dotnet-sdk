// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using FluentAssertions;
using Microsoft.NET.TestFramework;
using Microsoft.NET.TestFramework.Assertions;
using Microsoft.NET.TestFramework.Commands;
using Microsoft.TemplateEngine.TestHelper;
using VerifyTests;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.DotNet.New.Tests
{
    public partial class DotnetNewHelp : SdkTest, IClassFixture<SharedHomeDirectory>, IClassFixture<VerifySettingsFixture>
    {
        private readonly ITestOutputHelper _log;
        private readonly SharedHomeDirectory _fixture;
        private readonly VerifySettings _verifySettings;

        public DotnetNewHelp(SharedHomeDirectory fixture, VerifySettingsFixture verifySettings, ITestOutputHelper log) : base(log)
        {
            _log = log;
            _fixture = fixture;
            _verifySettings = verifySettings.Settings;
        }

        [Fact]
        public void WontShowLanguageHintInCaseOfOneLang()
        {
            string workingDirectory = TestUtils.CreateTemporaryFolder();

            new DotnetNewCommand(_log, "globaljson", "--help")
                    .WithCustomHive(_fixture.HomeDirectory)
                    .WithWorkingDirectory(workingDirectory)
                    .Execute()
                    .Should().Pass()
                    .And.NotHaveStdErr()
                    .And.HaveStdOutContaining("global.json file")
                    .And.NotHaveStdOutContaining("To see help for other template languages");
        }
    }
}
