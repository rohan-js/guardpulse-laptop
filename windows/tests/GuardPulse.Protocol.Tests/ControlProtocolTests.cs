namespace GuardPulse.Protocol.Tests;

using System.Collections.Generic;
using GuardPulse.Protocol;
using Xunit;

public class ControlProtocolTests
{
    // Valid devices/{id}/control/v2 payload. App entries are single-line JSON objects so every
    // mutation below is a newline-free substring replacement (robust to line-ending changes).
    // Keys: com.youtube.tv -> Y29tLnlvdXR1YmUudHY, com.netflix.app -> Y29tLm5ldGZsaXguYXBw,
    // com.disney.plus -> Y29tLmRpc25leS5wbHVz. The PIN is the deterministic v2 vector for 123456.
    private const string ValidJson = """
        {
          "schemaVersion": 2,
          "revisionId": "rev-100",
          "updatedAt": 1710000000000,
          "updatedBy": "parent-1",
          "apps": {
            "Y29tLnlvdXR1YmUudHY": { "packageKey": "Y29tLnlvdXR1YmUudHY", "packageName": "com.youtube.tv", "manualBlocked": true, "dailyLimitMinutes": 60, "updatedAt": 1710000000100 },
            "Y29tLm5ldGZsaXguYXBw": { "packageName": "com.netflix.app", "manualBlocked": false }
          },
          "modes": {
            "homework": { "modeId": "homework", "name": "Homework", "createdAt": 1710000000200, "updatedAt": 1710000000300, "apps": {
              "Y29tLnlvdXR1YmUudHY": { "packageName": "com.youtube.tv", "manualBlocked": false, "dailyLimitMinutes": 30 },
              "Y29tLmRpc25leS5wbHVz": { "packageName": "com.disney.plus", "manualBlocked": true }
            } }
          },
          "activeMode": { "modeId": "homework", "modeName": "Homework", "activatedAt": 1710000000400 },
          "safeMode": { "enabled": false, "until": 0, "startedAt": null, "startedBy": null },
          "pin": { "salt": "MDEyMzQ1Njc4OWFiY2RlZg", "hash": "9UI-qC10YGAs3EmhUfE4S5cFc2P_Psp3Kwc3_nbS1gc", "version": 2, "algorithm": "PBKDF2WithHmacSHA256", "iterations": 210000, "updatedAt": 1710000000500 }
        }
        """;

    private const string PinOneLiner =
        "\"pin\": { \"salt\": \"MDEyMzQ1Njc4OWFiY2RlZg\", \"hash\": \"9UI-qC10YGAs3EmhUfE4S5cFc2P_Psp3Kwc3_nbS1gc\", \"version\": 2, \"algorithm\": \"PBKDF2WithHmacSHA256\", \"iterations\": 210000, \"updatedAt\": 1710000000500 }";

    private static string Variant(string oldFragment, string? newFragment = null)
    {
        Assert.True(ValidJson.Contains(oldFragment), "fixture must contain fragment: " + oldFragment);
        return ValidJson.Replace(oldFragment, newFragment ?? string.Empty);
    }

    private static ControlParseResult ParseOk(string json)
    {
        var result = ControlProtocol.Parse(json);
        Assert.Equal(ControlParseStatus.Valid, result.Status);
        Assert.Null(result.Error);
        Assert.NotNull(result.Snapshot);
        return result;
    }

    private static void AssertInvalid(string json, string expectedError)
    {
        var result = ControlProtocol.Parse(json);
        Assert.Equal(ControlParseStatus.Invalid, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal(expectedError, result.Error);
    }


    // ---------- schedule / budget / contentFilter / allowlist ----------

    private static string WithControlField(string fieldJson)
    {
        return Variant("\"pin\":", $"{fieldJson},\n\"pin\":");
    }

    [Fact]
    public void ScheduleParsesWhenPresent()
    {
        var result = ParseOk(WithControlField("\"schedule\": { \"enabled\": true, \"startMinute\": 930, \"endMinute\": 1260 }"));
        var schedule = result.Snapshot!.Schedule;
        Assert.NotNull(schedule);
        Assert.True(schedule.Enabled);
        Assert.Equal(930, schedule.StartMinute);
        Assert.Equal(1260, schedule.EndMinute);
    }

    [Fact]
    public void ScheduleFieldsDefaultToNullWhenAbsent()
    {
        var result = ParseOk(ValidJson);
        Assert.Null(result.Snapshot!.Schedule);
        Assert.Null(result.Snapshot.Budget);
        Assert.Null(result.Snapshot.ContentFilter);
        Assert.Null(result.Snapshot.Allowlist);
    }

    [Theory]
    [InlineData("\"enabled\": true, \"startMinute\": -1, \"endMinute\": 60", "Schedule window is out of range")]
    [InlineData("\"enabled\": true, \"startMinute\": 0, \"endMinute\": 1440", "Schedule window is out of range")]
    [InlineData("\"enabled\": true, \"endMinute\": 60", "Schedule window is missing")]
    [InlineData("\"startMinute\": 0, \"endMinute\": 60", "Schedule enabled flag is missing")]
    public void ScheduleVariantsAreInvalid(string scheduleBody, string expectedError)
    {
        AssertInvalid(WithControlField($"\"schedule\": {{ {scheduleBody} }}"), expectedError);
    }

    [Fact]
    public void ScheduleMayWrapPastMidnight()
    {
        var result = ParseOk(WithControlField("\"schedule\": { \"enabled\": true, \"startMinute\": 1260, \"endMinute\": 420 }"));
        var schedule = result.Snapshot!.Schedule!;
        Assert.True(schedule.StartMinute > schedule.EndMinute); // 21:00 -> 07:00
    }

    [Fact]
    public void BudgetParsesWhenPresent()
    {
        var result = ParseOk(WithControlField("\"budget\": { \"dailyLimitMinutes\": 120 }"));
        Assert.Equal(120, result.Snapshot!.Budget!.DailyLimitMinutes);
    }

    [Theory]
    [InlineData(0, "Budget limit is out of range")]
    [InlineData(1441, "Budget limit is out of range")]
    [InlineData(-5, "Budget limit is out of range")]
    public void BudgetOutOfRangeIsInvalid(int minutes, string expectedError)
    {
        AssertInvalid(WithControlField($"\"budget\": {{ \"dailyLimitMinutes\": {minutes} }}"), expectedError);
    }

    [Fact]
    public void BudgetMissingLimitIsInvalid()
    {
        AssertInvalid(WithControlField("\"budget\": { }"), "Budget limit is missing");
    }

    [Fact]
    public void ContentFilterParsesWhenPresent()
    {
        var result = ParseOk(WithControlField("\"contentFilter\": { \"social\": true, \"gambling\": true, \"adult\": true, \"gaming\": false }"));
        var filter = result.Snapshot!.ContentFilter!;
        Assert.True(filter.Social);
        Assert.True(filter.Gambling);
        Assert.True(filter.Adult);
        Assert.False(filter.Gaming);
    }

    [Fact]
    public void ContentFilterMissingCategoryDefaultsToFalse()
    {
        var result = ParseOk(WithControlField("\"contentFilter\": { \"adult\": true }"));
        var filter = result.Snapshot!.ContentFilter!;
        Assert.True(filter.Adult);
        Assert.False(filter.Social);
        Assert.False(filter.Gambling);
        Assert.False(filter.Gaming);
    }

    [Theory]
    [InlineData("\"social\": \"maybe\"", "Content filter social flag is invalid")]
    [InlineData("\"gaming\": 5", "Content filter gaming flag is invalid")]
    public void ContentFilterBadFlagIsInvalid(string body, string expectedError)
    {
        AssertInvalid(WithControlField($"\"contentFilter\": {{ {body} }}"), expectedError);
    }

    [Fact]
    public void AllowlistParsesWhenPresent()
    {
        var result = ParseOk(WithControlField("\"allowlist\": { \"enabled\": true }"));
        Assert.True(result.Snapshot!.Allowlist!.Enabled);
    }

    [Fact]
    public void AllowlistMissingFlagIsInvalid()
    {
        AssertInvalid(WithControlField("\"allowlist\": { }"), "Allowlist enabled flag is missing");
    }

    // ---------- Missing / malformed input ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData(" null ")]
    public void MissingOrNullPayloadsAreMissing(string? json)
    {
        var result = ControlProtocol.Parse(json!);

        Assert.Equal(ControlParseStatus.Missing, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Null(result.Error);
    }

    [Fact]
    public void GarbageJsonIsInvalid()
    {
        var result = ControlProtocol.Parse("{oops");

        Assert.Equal(ControlParseStatus.Invalid, result.Status);
        Assert.Null(result.Snapshot);
        Assert.StartsWith("Control payload is not valid JSON", result.Error);
    }

    [Fact]
    public void NonObjectRootIsInvalid()
    {
        AssertInvalid("[1, 2, 3]", "Control snapshot is not an object");
    }

    // ---------- Schema / revision ----------

    [Fact]
    public void MissingSchemaVersionIsInvalid()
    {
        AssertInvalid(Variant("\"schemaVersion\": 2,"), "Unsupported control schema: missing");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void WrongSchemaVersionIsInvalid(int version)
    {
        AssertInvalid(
            Variant("\"schemaVersion\": 2,", "\"schemaVersion\": " + version + ","),
            "Unsupported control schema: " + version);
    }

    [Fact]
    public void MissingRevisionIdIsInvalid()
    {
        AssertInvalid(Variant("\"revisionId\": \"rev-100\","), "Control revision is missing");
    }

    [Fact]
    public void BlankRevisionIdIsInvalid()
    {
        AssertInvalid(
            Variant("\"revisionId\": \"rev-100\"", "\"revisionId\": \" \""),
            "Control revision is missing");
    }

    // ---------- App rules ----------

    [Fact]
    public void AppKeyMismatchIsInvalid()
    {
        AssertInvalid(
            Variant("\"Y29tLm5ldGZsaXguYXBw\": {", "\"aW52YWxpZA\": {"),
            "App policy key does not match package");
    }

    [Fact]
    public void ModeAppKeyMismatchIsInvalid()
    {
        AssertInvalid(
            Variant("\"Y29tLmRpc25leS5wbHVz\": {", "\"aW52YWxpZA\": {"),
            "App policy key does not match package");
    }

    [Fact]
    public void MissingPackageNameIsInvalid()
    {
        AssertInvalid(
            Variant("{ \"packageName\": \"com.netflix.app\", \"manualBlocked\": false }", "{ \"manualBlocked\": false }"),
            "App package is missing");
    }

    [Fact]
    public void MissingManualBlockedFlagIsInvalid()
    {
        AssertInvalid(
            Variant("\"packageName\": \"com.netflix.app\", \"manualBlocked\": false }", "\"packageName\": \"com.netflix.app\" }"),
            "App manualBlocked flag is missing");
    }

    [Fact]
    public void NonNumericDailyLimitIsInvalid()
    {
        AssertInvalid(
            Variant("\"dailyLimitMinutes\": 60", "\"dailyLimitMinutes\": \"sixty\""),
            "App daily limit is invalid");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1441)]
    public void OutOfRangeDailyLimitIsInvalid(int minutes)
    {
        AssertInvalid(
            Variant("\"dailyLimitMinutes\": 60", "\"dailyLimitMinutes\": " + minutes),
            "App daily limit is out of range");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1440)]
    public void BoundaryDailyLimitsAreValid(int minutes)
    {
        var result = ParseOk(Variant("\"dailyLimitMinutes\": 60", "\"dailyLimitMinutes\": " + minutes));

        Assert.Equal(minutes, result.Snapshot!.Apps["com.youtube.tv"].DailyLimitMinutes);
    }

    [Fact]
    public void NonNumericSessionLimitIsInvalid()
    {
        AssertInvalid(
            Variant("\"dailyLimitMinutes\": 60", "\"dailyLimitMinutes\": 60, \"sessionLimitMinutes\": \"forty\""),
            "App session limit is invalid");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1441)]
    public void OutOfRangeSessionLimitIsInvalid(int minutes)
    {
        AssertInvalid(
            Variant("\"dailyLimitMinutes\": 60", "\"dailyLimitMinutes\": 60, \"sessionLimitMinutes\": " + minutes),
            "App session limit is out of range");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1440)]
    public void BoundarySessionLimitsAreValid(int minutes)
    {
        var result = ParseOk(Variant("\"dailyLimitMinutes\": 60", "\"dailyLimitMinutes\": 60, \"sessionLimitMinutes\": " + minutes));

        Assert.Equal(minutes, result.Snapshot!.Apps["com.youtube.tv"].SessionLimitMinutes);
    }

    [Fact]
    public void SessionLimitParsesInsideModeApp()
    {
        var result = ParseOk(Variant(
            "\"packageName\": \"com.youtube.tv\", \"manualBlocked\": false, \"dailyLimitMinutes\": 30 }",
            "\"packageName\": \"com.youtube.tv\", \"manualBlocked\": false, \"dailyLimitMinutes\": 30, \"sessionLimitMinutes\": 45 }"));

        Assert.Equal(
            45,
            result.Snapshot!.Modes["homework"].Apps["com.youtube.tv"].SessionLimitMinutes);
    }

    [Fact]
    public void PackageKeyFieldMismatchIsInvalid()
    {
        AssertInvalid(
            Variant("\"packageKey\": \"Y29tLnlvdXR1YmUudHY\"", "\"packageKey\": \"b3RoZXJrZXk\""),
            "App package key does not match policy key");
    }

    // ---------- Modes (lenient: a malformed mode is dropped, not fatal) ----------

    [Fact]
    public void ModeKeyMismatchIsDropped()
    {
        var result = ParseOk(Variant("\"homework\": { \"modeId\": \"homework\"", "\"other\": { \"modeId\": \"homework\""));

        Assert.Empty(result.Snapshot!.Modes);
        Assert.Null(result.Snapshot.ActiveMode); // active mode referenced the dropped mode
    }

    [Fact]
    public void BlankModeIdIsDropped()
    {
        var result = ParseOk(Variant("\"homework\": { \"modeId\": \"homework\"", "\"homework\": { \"modeId\": \" \""));

        Assert.Empty(result.Snapshot!.Modes);
    }

    [Fact]
    public void MissingModeNameIsDropped()
    {
        var result = ParseOk(Variant("\"modeId\": \"homework\", \"name\": \"Homework\"", "\"modeId\": \"homework\""));

        Assert.Empty(result.Snapshot!.Modes);
    }

    [Fact]
    public void BlankModeNameIsDropped()
    {
        var result = ParseOk(Variant("\"name\": \"Homework\"", "\"name\": \"   \""));

        Assert.Empty(result.Snapshot!.Modes);
    }

    // ---------- Active mode (lenient: an unparseable/missing activeMode is dropped) ----------

    [Fact]
    public void MissingActiveModeIdIsDropped()
    {
        var result = ParseOk(Variant("\"activeMode\": { \"modeId\": \"homework\",", "\"activeMode\": {"));

        Assert.Null(result.Snapshot!.ActiveMode);
    }

    [Fact]
    public void UnknownActiveModeIsDropped()
    {
        var result = ParseOk(Variant("\"activeMode\": { \"modeId\": \"homework\"", "\"activeMode\": { \"modeId\": \"ghost\""));

        Assert.Null(result.Snapshot!.ActiveMode);
    }

    // ---------- Safe mode (lenient: a malformed/missing safeMode defaults to disabled) ----------

    [Fact]
    public void MissingSafeModeDefaultsToDisabled()
    {
        var result = ParseOk(Variant("\"safeMode\": { \"enabled\": false, \"until\": 0, \"startedAt\": null, \"startedBy\": null },"));

        Assert.False(result.Snapshot!.SafeMode.Enabled);
        Assert.Equal(0L, result.Snapshot.SafeMode.Until);
    }

    [Fact]
    public void MissingSafeModeEnabledFlagDefaultsToDisabled()
    {
        var result = ParseOk(Variant("\"safeMode\": { \"enabled\": false,", "\"safeMode\": {"));

        Assert.False(result.Snapshot!.SafeMode.Enabled);
        Assert.Equal(0L, result.Snapshot.SafeMode.Until);
    }

    [Fact]
    public void MissingSafeModeExpiryDefaultsToDisabled()
    {
        var result = ParseOk(Variant("{ \"enabled\": false, \"until\": 0,", "{ \"enabled\": false,"));

        Assert.False(result.Snapshot!.SafeMode.Enabled);
        Assert.Equal(0L, result.Snapshot.SafeMode.Until);
    }

    [Fact]
    public void EnabledSafeModeWithInvalidExpiryDefaultsToDisabled()
    {
        var result = ParseOk(Variant("\"enabled\": false, \"until\": 0", "\"enabled\": true, \"until\": 0"));

        // An enabled window with no/invalid positive deadline is tolerated as disabled, not fatal.
        Assert.False(result.Snapshot!.SafeMode.Enabled);
        Assert.Equal(0L, result.Snapshot.SafeMode.Until);
    }

    [Fact]
    public void EnabledSafeModeWithFutureExpiryIsValid()
    {
        var result = ParseOk(Variant("\"enabled\": false, \"until\": 0", "\"enabled\": true, \"until\": 1710000600000"));

        Assert.True(result.Snapshot!.SafeMode.Enabled);
        Assert.Equal(1710000600000L, result.Snapshot.SafeMode.Until);
    }

    // ---------- PIN (lenient: a malformed pin is dropped, not fatal) ----------

    [Fact]
    public void MissingPinSaltIsDropped()
    {
        var result = ParseOk(Variant("\"salt\": \"MDEyMzQ1Njc4OWFiY2RlZg\","));

        Assert.Null(result.Snapshot!.Pin);
    }

    [Fact]
    public void BadPinSaltShapeIsDropped()
    {
        // 18-char salt (legacy shape) violates the 16-byte/22-char v2-control shape rule.
        var result = ParseOk(Variant("\"salt\": \"MDEyMzQ1Njc4OWFiY2RlZg\"", "\"salt\": \"c2FsdC1mb3ItdGVzdA\""));

        Assert.Null(result.Snapshot!.Pin);
    }

    [Fact]
    public void MissingPinHashIsDropped()
    {
        var result = ParseOk(Variant("\"hash\": \"9UI-qC10YGAs3EmhUfE4S5cFc2P_Psp3Kwc3_nbS1gc\","));

        Assert.Null(result.Snapshot!.Pin);
    }

    [Fact]
    public void BadPinHashShapeIsDropped()
    {
        // 42 chars instead of 43.
        var result = ParseOk(Variant("\"hash\": \"9UI-qC10YGAs3EmhUfE4S5cFc2P_Psp3Kwc3_nbS1gc\"", "\"hash\": \"9UI-qC10YGAs3EmhUfE4S5cFc2P_Psp3Kwc3_nbS1g\""));

        Assert.Null(result.Snapshot!.Pin);
    }

    [Fact]
    public void UnsupportedPinVersionIsDropped()
    {
        var result = ParseOk(Variant("\"version\": 2,", "\"version\": 3,"));

        Assert.Null(result.Snapshot!.Pin);
    }

    [Fact]
    public void WrongPinAlgorithmIsDropped()
    {
        var result = ParseOk(Variant("\"algorithm\": \"PBKDF2WithHmacSHA256\"", "\"algorithm\": \"SHA-256\""));

        Assert.Null(result.Snapshot!.Pin);
    }

    [Fact]
    public void MissingPinIterationsIsDropped()
    {
        var result = ParseOk(Variant("\"algorithm\": \"PBKDF2WithHmacSHA256\", \"iterations\": 210000,", "\"algorithm\": \"PBKDF2WithHmacSHA256\","));

        Assert.Null(result.Snapshot!.Pin);
    }

    [Theory]
    [InlineData(209999)]
    [InlineData(1000001)]
    public void OutOfRangePinIterationsIsDropped(int iterations)
    {
        var result = ParseOk(Variant("\"iterations\": 210000", "\"iterations\": " + iterations));

        Assert.Null(result.Snapshot!.Pin);
    }

    [Fact]
    public void UpperBoundPinIterationsIsValid()
    {
        var result = ParseOk(Variant("\"iterations\": 210000", "\"iterations\": 1000000"));

        Assert.Equal(1000000, result.Snapshot!.Pin!.Iterations);
    }

    [Fact]
    public void MissingPinIsValid()
    {
        var result = ParseOk(Variant(PinOneLiner, "\"pinless\": true"));

        Assert.Null(result.Snapshot!.Pin);
    }

    [Fact]
    public void LegacyV1PinIsValid()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "revisionId": "rev-legacy",
              "safeMode": { "enabled": false, "until": 0 },
              "pin": { "salt": "MDEyMzQ1Njc4OWFiY2RlZg", "hash": "0SKIzDQHiAdIWhNM3K0h0TiekpGhVd6aEfGNBELFhak", "version": 1 }
            }
            """;

        var result = ParseOk(json);

        Assert.Equal(PinHasher.LEGACY_VERSION, result.Snapshot!.Pin!.Version);
        Assert.Null(result.Snapshot.Pin.Algorithm);
        Assert.Null(result.Snapshot.Pin.Iterations);
        Assert.True(PinHasher.Verify("123456", result.Snapshot.Pin.Salt, result.Snapshot.Pin.Hash, PinHasher.LEGACY_VERSION));
    }

    // ---------- Valid snapshot mapping ----------

    [Fact]
    public void ValidSnapshotParsesEverySection()
    {
        var result = ParseOk(ValidJson);
        var snapshot = result.Snapshot!;

        Assert.Equal("rev-100", snapshot.RevisionId);
        Assert.Equal(1710000000000L, snapshot.UpdatedAt);
        Assert.Equal("parent-1", snapshot.UpdatedBy);

        Assert.Equal(2, snapshot.Apps.Count);
        var youtube = snapshot.Apps["com.youtube.tv"];
        Assert.True(youtube.ManualBlocked);
        Assert.Equal(60, youtube.DailyLimitMinutes);
        Assert.Equal(1710000000100L, youtube.UpdatedAt);
        var netflix = snapshot.Apps["com.netflix.app"];
        Assert.False(netflix.ManualBlocked);
        Assert.Null(netflix.DailyLimitMinutes);
        Assert.Null(netflix.UpdatedAt);

        var mode = snapshot.Modes["homework"];
        Assert.Equal("homework", mode.ModeId);
        Assert.Equal("Homework", mode.Name);
        Assert.Equal(1710000000200L, mode.CreatedAt);
        Assert.Equal(1710000000300L, mode.UpdatedAt);
        Assert.Equal(2, mode.Apps.Count);
        Assert.False(mode.Apps["com.youtube.tv"].ManualBlocked);
        Assert.Equal(30, mode.Apps["com.youtube.tv"].DailyLimitMinutes);
        Assert.True(mode.Apps["com.disney.plus"].ManualBlocked);

        Assert.Equal("homework", snapshot.ActiveMode!.ModeId);
        Assert.Equal("Homework", snapshot.ActiveMode.ModeName);
        Assert.Equal(1710000000400L, snapshot.ActiveMode.ActivatedAt);

        Assert.False(snapshot.SafeMode.Enabled);
        Assert.Equal(0L, snapshot.SafeMode.Until);
        Assert.Null(snapshot.SafeMode.StartedAt);
        Assert.Null(snapshot.SafeMode.StartedBy);

        var pin = snapshot.Pin!;
        Assert.Equal("MDEyMzQ1Njc4OWFiY2RlZg", pin.Salt);
        Assert.Equal("9UI-qC10YGAs3EmhUfE4S5cFc2P_Psp3Kwc3_nbS1gc", pin.Hash);
        Assert.Equal(PinHasher.CURRENT_VERSION, pin.Version);
        Assert.Equal(PinHasher.ALGORITHM, pin.Algorithm);
        Assert.Equal(210000, pin.Iterations);
        Assert.Equal(1710000000500L, pin.UpdatedAt);

        // The fixture PIN is the deterministic v2 vector for 123456.
        Assert.True(PinHasher.Verify("123456", pin.Salt, pin.Hash, pin.Version, pin.Algorithm, pin.Iterations));
    }

    // ---------- EffectiveApps ----------

    [Fact]
    public void EffectiveAppsUsesActiveModeAppsPlusDefaults()
    {
        var snapshot = ParseOk(ValidJson).Snapshot!;
        var effective = snapshot.EffectiveApps();

        var expectedCount = 2 /* mode apps */ + PolicyConstants.DefaultLockedPackages.Count;
        Assert.Equal(expectedCount, effective.Count);

        // The active mode overrides the top-level rule (blocked/60 -> unblocked/30).
        Assert.False(effective["com.youtube.tv"].ManualBlocked);
        Assert.Equal(30, effective["com.youtube.tv"].DailyLimitMinutes);
        Assert.True(effective["com.disney.plus"].ManualBlocked);
        Assert.DoesNotContain("com.netflix.app", effective.Keys);

        foreach (var packageName in PolicyConstants.DefaultLockedPackages)
        {
            Assert.True(effective[packageName].ManualBlocked, packageName + " should be blocked");
            Assert.Null(effective[packageName].DailyLimitMinutes);
        }
    }

    [Fact]
    public void EffectiveAppsFallsBackToTopLevelAppsWithoutActiveMode()
    {
        var json = Variant("\"activeMode\": { \"modeId\": \"homework\", \"modeName\": \"Homework\", \"activatedAt\": 1710000000400 },");
        var snapshot = ParseOk(json).Snapshot!;
        Assert.Null(snapshot.ActiveMode);

        var effective = snapshot.EffectiveApps();
        var expectedCount = 2 /* top-level apps */ + PolicyConstants.DefaultLockedPackages.Count;
        Assert.Equal(expectedCount, effective.Count);

        Assert.True(effective["com.youtube.tv"].ManualBlocked);
        Assert.Equal(60, effective["com.youtube.tv"].DailyLimitMinutes);
        Assert.False(effective["com.netflix.app"].ManualBlocked);
    }

    [Fact]
    public void EffectiveAppsIgnoresActiveModeWithoutMatchingModeRecord()
    {
        // Constructed (not parsed) snapshot: ActiveMode references a mode that is absent,
        // so the top-level apps are used, with defaults still filled in.
        var snapshot = new ControlSnapshotV2(
            "rev-direct",
            UpdatedAt: null,
            UpdatedBy: null,
            new Dictionary<string, ControlAppRule>
            {
                ["com.youtube.tv"] = new ControlAppRule("com.youtube.tv", ManualBlocked: false),
            },
            new Dictionary<string, ControlMode>
            {
                ["homework"] = new ControlMode(
                    "homework",
                    "Homework",
                    new Dictionary<string, ControlAppRule>
                    {
                        ["com.disney.plus"] = new ControlAppRule("com.disney.plus", ManualBlocked: true),
                    }),
            },
            new ControlActiveMode("ghost"),
            new ControlSafeMode(),
            Pin: null);

        var effective = snapshot.EffectiveApps();
        var expectedCount = 1 + PolicyConstants.DefaultLockedPackages.Count;
        Assert.Equal(expectedCount, effective.Count);
        Assert.False(effective["com.youtube.tv"].ManualBlocked);
        Assert.DoesNotContain("com.disney.plus", effective.Keys);
    }

    // ---------- ParseDesired ----------

    [Theory]
    [InlineData("appPolicy")]
    [InlineData("modeCreate")]
    [InlineData("modeUpdate")]
    [InlineData("modeDelete")]
    [InlineData("modePolicy")]
    [InlineData("activeMode")]
    [InlineData("safeMode")]
    [InlineData("pin")]
    [InlineData("migration")]
    public void ParseDesiredAcceptsKnownKinds(string kind)
    {
        var json = "{\"revisionId\":\"rev-9\",\"kind\":\"" + kind + "\",\"target\":\"com.youtube.tv\",\"requestedAt\":1710000000,\"requestedBy\":\"parent-1\"}";

        var desired = ControlProtocol.ParseDesired(json);

        Assert.NotNull(desired);
        Assert.Equal("rev-9", desired!.RevisionId);
        Assert.Equal(kind, desired.Kind);
        Assert.Equal("com.youtube.tv", desired.Target);
        Assert.Equal(1710000000L, desired.RequestedAt);
        Assert.Equal("parent-1", desired.RequestedBy);
    }

    [Fact]
    public void ParseDesiredMapsMinimalPayload()
    {
        var desired = ControlProtocol.ParseDesired("{\"revisionId\":\"rev-9\",\"kind\":\"safeMode\"}");

        Assert.NotNull(desired);
        Assert.Equal("rev-9", desired!.RevisionId);
        Assert.Equal("safeMode", desired.Kind);
        Assert.Null(desired.Target);
        Assert.Null(desired.RequestedAt);
        Assert.Null(desired.RequestedBy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{oops")]
    [InlineData("{\"kind\":\"appPolicy\"}")]
    [InlineData("{\"revisionId\":\"rev-9\",\"kind\":\"bogus\"}")]
    [InlineData("{\"revisionId\":\"  \",\"kind\":\"appPolicy\"}")]
    [InlineData("{\"revisionId\":\"rev-9\"}")]
    public void ParseDesiredReturnsNullForAbsentOrUnknownPayloads(string? json)
    {
        Assert.Null(ControlProtocol.ParseDesired(json!));
    }
}
