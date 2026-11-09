// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Build.Internal;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests;

public class CommunicationUtilitiesTests
{
    /// <summary>
    /// Verify our custom way of getting env vars gives the same results as the BCL.
    /// </summary>
    [Fact]
    public void GetEnvVars()
    {
        IDictionary<string, string> envVars = CommunicationsUtilities.GetEnvironmentVariables();
        IDictionary referenceVars = Environment.GetEnvironmentVariables();

        // Our dictionary ignores case in names, so variables whose names differ only by case (possible on Unix)
        // collapse into a single entry. Which name and which value of such a group is kept is unspecified.
        Dictionary<string, List<string>> referenceValuesByName = new(CommunicationsUtilities.EnvironmentVariableComparer);
        foreach (DictionaryEntry item in referenceVars)
        {
            string name = (string)item.Key;
            if (!referenceValuesByName.TryGetValue(name, out List<string>? values))
            {
                values = [];
                referenceValuesByName.Add(name, values);
            }

            values.Add((string?)item.Value ?? string.Empty);
        }

        // One entry for every name that is distinct ignoring case...
        envVars.Count.ShouldBe(referenceValuesByName.Count);

        // ...and every entry has a name the BCL reports and a value the BCL reports for that name ignoring case.
        // For a name that is unique ignoring case, this is exactly the BCL's value.
        foreach (KeyValuePair<string, string> envVar in envVars)
        {
            referenceVars.Contains(envVar.Key).ShouldBeTrue(envVar.Key);
            referenceValuesByName[envVar.Key].ShouldContain(envVar.Value, envVar.Key);
        }
    }

    /// <summary>
    /// Verify that we correctly restore environment variables.
    /// </summary>
    [Fact]
    public void RestoreEnvVars()
    {
        string testName1 = "_MSBUILD_TEST_ENV_VAR1";
        string testName2 = "_MSBUILD_TEST_ENV_VAR2";

        // A long value exceeding the former limit of 32,767 characters.
        string testValue = new string('a', 1_000_000);

        CommunicationsUtilities.SetEnvironmentVariable(testName1, testValue);
        try
        {
            IDictionary<string, string> envVars = CommunicationsUtilities.GetEnvironmentVariables();

            CommunicationsUtilities.SetEnvironmentVariable(testName1, null);
            CommunicationsUtilities.SetEnvironmentVariable(testName2, testValue);

            CommunicationsUtilities.SetEnvironment(envVars);

            Environment.GetEnvironmentVariable(testName1).ShouldBe(testValue);
            Environment.GetEnvironmentVariable(testName2).ShouldBe(null);
        }
        finally
        {
            CommunicationsUtilities.SetEnvironmentVariable(testName1, null);
            CommunicationsUtilities.SetEnvironmentVariable(testName2, null);
        }
    }
}
