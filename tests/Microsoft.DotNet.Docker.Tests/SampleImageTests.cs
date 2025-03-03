﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.DotNet.Docker.Tests
{
    [Trait("Category", "sample")]
    public class SampleImageTests
    {
        private static readonly string s_samplesPath = Path.Combine(Config.SourceRepoRoot, "samples");

        public SampleImageTests(ITestOutputHelper outputHelper)
        {
            OutputHelper = outputHelper;
            DockerHelper = new DockerHelper(outputHelper);
        }

        protected DockerHelper DockerHelper { get; }

        protected ITestOutputHelper OutputHelper { get; }

        public static IEnumerable<object[]> GetImageData()
        {
            return TestData.GetSampleImageData()
                .Select(imageData => new object[] { imageData });
        }

        [DotNetTheory]
        [MemberData(nameof(GetImageData))]
        public async Task VerifyDotnetSample(SampleImageData imageData)
        {
            if (imageData.DockerfileSuffix == "windowsservercore-iis")
            {
                return;
            }

            await VerifySampleAsync(imageData, SampleImageType.Dotnetapp, (image, containerName) =>
            {
                string output = DockerHelper.Run(image, containerName);
                Assert.True(output.Contains("42") || output.StartsWith("Hello"));

                ValidateEnvironmentVariables(imageData, image, SampleImageType.Dotnetapp);

                return Task.CompletedTask;
            });
        }

        [DotNetTheory]
        [MemberData(nameof(GetImageData))]
        public async Task VerifyAspnetSample(SampleImageData imageData)
        {
            await VerifySampleAsync(imageData, SampleImageType.Aspnetapp, async (image, containerName) =>
            {
                int port = imageData.DockerfileSuffix == "windowsservercore-iis" ? 80 : imageData.DefaultPort;

                try
                {
                    DockerHelper.Run(
                        image: image,
                        name: containerName,
                        detach: true,
                        optionalRunArgs: $"-p {port}",
                        skipAutoCleanup: true);

                    if (!Config.IsHttpVerificationDisabled)
                    {
                        await WebScenario.VerifyHttpResponseFromContainerAsync(containerName, DockerHelper, OutputHelper, port);
                    }

                    ValidateEnvironmentVariables(imageData, image, SampleImageType.Aspnetapp);
                }
                finally
                {
                    DockerHelper.DeleteContainer(containerName);
                }
            });
        }

        [Fact]
        public void VerifyComplexAppSample()
        {
            // complexapp sample doesn't currently support building on Windows.
            if (!DockerHelper.IsLinuxContainerModeEnabled)
            {
                return;
            }

            string appTag = SampleImageData.GetImageName("complexapp-local-app");
            string testTag = SampleImageData.GetImageName("complexapp-local-test");
            string sampleFolder = Path.Combine(s_samplesPath, "complexapp");
            string dockerfilePath = $"{sampleFolder}/Dockerfile";
            string testContainerName = ImageData.GenerateContainerName("sample-complex-test");
            string tempDir = null;
            try
            {
                // Test that the app works
                DockerHelper.Build(appTag, dockerfilePath, contextDir: sampleFolder, pull: Config.PullImages);
                string containerName = ImageData.GenerateContainerName("sample-complex");
                string output = DockerHelper.Run(appTag, containerName);
                Assert.StartsWith("string: The quick brown fox jumps over the lazy dog", output);

                // Run the app's tests
                DockerHelper.Build(testTag, dockerfilePath, target: "test", contextDir: sampleFolder);
                DockerHelper.Run(testTag, testContainerName, skipAutoCleanup: true);

                // Copy the test log from the container to the host
                tempDir = Directory.CreateDirectory(
                    Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
                DockerHelper.Copy($"{testContainerName}:/source/tests/TestResults", tempDir);
                string testLogFile = new DirectoryInfo($"{tempDir}/TestResults").GetFiles("*.trx").First().FullName;

                // Open the test log file and verify the tests passed
                XDocument doc = XDocument.Load(testLogFile);
                XElement summary = doc.Root.Element(XName.Get("ResultSummary", doc.Root.Name.NamespaceName));
                Assert.Equal("Completed", summary.Attribute("outcome").Value);
                XElement counters = summary.Element(XName.Get("Counters", doc.Root.Name.NamespaceName));
                Assert.Equal("3", counters.Attribute("total").Value);
                Assert.Equal("3", counters.Attribute("passed").Value);
            }
            finally
            {
                if (tempDir != null)
                {
                    Directory.Delete(tempDir, true);
                }

                DockerHelper.DeleteContainer(testContainerName);
                DockerHelper.DeleteImage(testTag);
                DockerHelper.DeleteImage(appTag);
            }
        }

        private async Task VerifySampleAsync(
            SampleImageData imageData,
            SampleImageType sampleImageType,
            Func<string, string, Task> verifyImageAsync)
        {
            string image = imageData.GetImage(sampleImageType, DockerHelper);
            string imageType = Enum.GetName(typeof(SampleImageType), sampleImageType).ToLowerInvariant();
            try
            {
                if (!imageData.IsPublished)
                {
                    string sampleFolder = Path.Combine(s_samplesPath, imageType);
                    string dockerfilePath = $"{sampleFolder}/Dockerfile";
                    if (!string.IsNullOrEmpty(imageData.DockerfileSuffix))
                    {
                        dockerfilePath += $".{imageData.DockerfileSuffix}";
                    }

                    DockerHelper.Build(image, dockerfilePath, contextDir: sampleFolder, pull: Config.PullImages, platform: imageData.Platform);
                }

                string containerName = imageData.GetIdentifier($"sample-{imageType}");
                await verifyImageAsync(image, containerName);
            }
            finally
            {
                if (!imageData.IsPublished)
                {
                    DockerHelper.DeleteImage(image);
                }
            }
        }

        private void ValidateEnvironmentVariables(SampleImageData imageData, string image, SampleImageType imageType)
        {
            List<EnvironmentVariableInfo> variables = new List<EnvironmentVariableInfo>();
            variables.AddRange(ProductImageTests.GetCommonEnvironmentVariables());

            if (imageType == SampleImageType.Aspnetapp)
            {
                variables.Add(new EnvironmentVariableInfo("ASPNETCORE_HTTP_PORTS", imageData.DefaultPort.ToString()));
            }

            EnvironmentVariableInfo.Validate(
                variables,
                image,
                imageData,
                DockerHelper);
        }
    }
}
