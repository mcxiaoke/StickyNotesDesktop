using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Services;

namespace StickyNotes.Tests;

/// <summary>
/// PinService 单元测试：设置/验证/清除、长度校验、持久化与篡改防护
/// </summary>
[TestClass]
public class PinServiceTests
{
    private string _testDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"StickyNotes_PinTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    private (SettingsService Settings, PinService Pin) CreateService(string fileName)
    {
        var settings = new SettingsService(Path.Combine(_testDir, fileName));
        return (settings, new PinService(settings));
    }

    [TestMethod]
    public void SetPin_ThenVerify_ShouldRoundTrip()
    {
        var (_, pin) = CreateService("s1.json");

        Assert.IsFalse(pin.IsPinSet);
        Assert.IsFalse(pin.IsPinEnabled);

        Assert.IsTrue(pin.SetPin("abcd1234"));

        Assert.IsTrue(pin.IsPinSet);
        Assert.IsTrue(pin.IsPinEnabled);
        Assert.IsTrue(pin.VerifyPin("abcd1234"));
        Assert.IsFalse(pin.VerifyPin("abcd1235"));
        Assert.IsFalse(pin.VerifyPin(null));
        Assert.IsFalse(pin.VerifyPin(string.Empty));
    }

    [TestMethod]
    public void SetPin_ShouldNotStorePlaintext()
    {
        var (settings, pin) = CreateService("s2.json");

        Assert.IsTrue(pin.SetPin("my-secret-pin"));

        // settings.json 中不得出现明文 PIN
        var json = File.ReadAllText(Path.Combine(_testDir, "s2.json"));
        Assert.IsTrue(json.Contains("PinHash"));
        Assert.IsFalse(json.Contains("my-secret-pin"));
        Assert.AreNotEqual("my-secret-pin", settings.Settings.PinHash);
    }

    [TestMethod]
    public void SetPin_LengthValidation_ShouldReject()
    {
        var (_, pin) = CreateService("s3.json");

        Assert.IsFalse(pin.SetPin("abc"));                    // 少于 4 位
        Assert.IsFalse(pin.SetPin(""));                       // 空
        Assert.IsFalse(pin.SetPin(new string('x', 21)));      // 超过 20 位

        Assert.IsTrue(pin.SetPin("1234"));                    // 恰好 4 位
        Assert.IsTrue(pin.SetPin(new string('x', 20)));       // 恰好 20 位

        Assert.IsTrue(PinService.IsValidPin("1234"));
        Assert.IsTrue(PinService.IsValidPin("p@ss word!中文字符"));
        Assert.IsFalse(PinService.IsValidPin("123"));
        Assert.IsFalse(PinService.IsValidPin(null));
    }

    [TestMethod]
    public void ClearPin_WithWrongCurrentPin_ShouldRefuse()
    {
        var (_, pin) = CreateService("s4.json");

        Assert.IsTrue(pin.SetPin("9999"));
        Assert.IsFalse(pin.ClearPin("0000"));
        Assert.IsTrue(pin.IsPinEnabled);
        Assert.IsTrue(pin.VerifyPin("9999"));
    }

    [TestMethod]
    public void ClearPin_WithCorrectCurrentPin_ShouldDisable()
    {
        var (_, pin) = CreateService("s5.json");

        Assert.IsTrue(pin.SetPin("9999"));
        Assert.IsTrue(pin.ClearPin("9999"));

        Assert.IsFalse(pin.IsPinSet);
        Assert.IsFalse(pin.IsPinEnabled);
        Assert.IsFalse(pin.VerifyPin("9999"));
    }

    [TestMethod]
    public void ChangePin_ShouldReplaceOldPin()
    {
        var (_, pin) = CreateService("s6.json");

        Assert.IsTrue(pin.SetPin("1111"));
        Assert.IsTrue(pin.SetPin("2222"));

        Assert.IsTrue(pin.VerifyPin("2222"));
        Assert.IsFalse(pin.VerifyPin("1111"));
    }

    [TestMethod]
    public void Pin_PersistenceAcrossInstances_ShouldSurviveReload()
    {
        var (settings, pin) = CreateService("s7.json");
        Assert.IsTrue(pin.SetPin("persist-1"));
        Assert.AreEqual(10, settings.IdleLockMinutes);      // 默认闲置 10 分钟锁定
        Assert.IsTrue(settings.LockOnLaunch);              // 冷启动默认锁定（防退出再重开绕过）

        // 模拟应用重启：重新加载同一 settings.json
        var settings2 = new SettingsService(Path.Combine(_testDir, "s7.json"));
        var pin2 = new PinService(settings2);

        Assert.IsTrue(pin2.IsPinEnabled);
        Assert.IsTrue(pin2.VerifyPin("persist-1"));
        Assert.IsFalse(pin2.VerifyPin("persist-2"));
    }

    [TestMethod]
    public void VerifyPin_CorruptedHash_ShouldFailSafely()
    {
        var (settings, pin) = CreateService("s8.json");

        Assert.IsTrue(pin.SetPin("abcd"));
        settings.Settings.PinHash = "!!!不是base64!!!";

        Assert.IsFalse(pin.VerifyPin("abcd"));
        // 数据损坏时开关状态保持，但验证一律失败（安全降级）
        Assert.IsTrue(pin.IsPinEnabled);
    }

    [TestMethod]
    public void PinEnabled_WithoutPinData_ShouldNotTakeEffect()
    {
        var (settings, pin) = CreateService("s9.json");

        // settings.json 被手动改出 PinEnabled=true 但无盐无哈希（用户重置 PIN 的方式）
        settings.Settings.PinEnabled = true;

        Assert.IsFalse(pin.IsPinEnabled);
        Assert.IsFalse(pin.VerifyPin("anything"));
    }

    [TestMethod]
    public void SetPin_WithSamePinTwice_ShouldUseDifferentSalt()
    {
        var (settings, pin) = CreateService("s10.json");

        Assert.IsTrue(pin.SetPin("abcd"));
        var hash1 = settings.Settings.PinHash;
        var salt1 = settings.Settings.PinSalt;

        Assert.IsTrue(pin.SetPin("abcd"));
        var hash2 = settings.Settings.PinHash;

        // 相同 PIN 两次设置应产生不同盐与不同哈希
        Assert.AreNotEqual(salt1, settings.Settings.PinSalt);
        Assert.AreNotEqual(hash1, hash2);
        Assert.IsTrue(pin.VerifyPin("abcd"));
    }
}
