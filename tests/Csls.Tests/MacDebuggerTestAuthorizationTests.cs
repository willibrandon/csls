using Csls.Support;
using System.Xml;
using System.Xml.Linq;

namespace Csls.Tests;

/// <summary>
/// Exercises debugger authorization parsing with captured runner output and hostile policy files.
/// </summary>
[TestClass]
public sealed class MacDebuggerTestAuthorizationTests
{
    /// <summary>
    /// Preserves the captured runner policy while changing its interactive authentication flag.
    /// </summary>
    [TestMethod]
    public void PreservesIndentedRunnerPolicyAndGroupRestriction()
    {
        string original = File.ReadAllText(PolicyPath);
        string enabled = MacDebuggerTestAuthorization.CreatePolicy(original);
        var expected = XDocument.Parse(original);
        XElement dictionary = expected.Element("plist")?.Element("dict") ??
            throw new InvalidDataException("The captured policy has no dictionary.");
        XElement authentication = dictionary.Elements("key")
            .Single(key => key.Value == "authenticate-user").ElementsAfterSelf().First();
        Assert.AreEqual("true", authentication.Name.LocalName);
        authentication.Name = "false";
        var actual = XDocument.Parse(enabled);
        Assert.IsTrue(XNode.DeepEquals(expected.Root, actual.Root),
            "Only the authentication flag may change in the saved runner policy.");
        MacDebuggerTestAuthorization.VerifyPolicy(expected.ToString(), enabled);
        MacDebuggerTestAuthorization.VerifyPolicy(original, File.ReadAllText(PolicyPath));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            MacDebuggerTestAuthorization.VerifyPolicy(original, enabled));
    }

    /// <summary>
    /// Rejects malformed and broadened policy rules read through a real temporary file.
    /// </summary>
    [TestMethod]
    [DataRow("<string>user</string>", "<string>allow</string>")]
    [DataRow("<string>_developer</string>", "<string>admin</string>")]
    [DataRow("<key>class</key>", "<key>wrong-key</key>")]
    [DataRow("<key>authenticate-user</key>\n\t<true/>", "<key>authenticate-user</key>\n\t<string>true</string>")]
    public void RejectsUnexpectedAuthorizationRules(string originalText, string hostileText)
    {
        string original = File.ReadAllText(PolicyPath);
        Assert.Contains(originalText, original);
        string path = Path.Join(Path.GetTempPath(), $"csls-taskport-{Guid.NewGuid():N}.plist");
        try
        {
            File.WriteAllText(path, original.Replace(originalText, hostileText, StringComparison.Ordinal));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                MacDebuggerTestAuthorization.CreatePolicy(File.ReadAllText(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Rejects an external entity in a policy file without resolving its local file reference.
    /// </summary>
    [TestMethod]
    public void RejectsExternalPolicyEntities()
    {
        string path = Path.Join(Path.GetTempPath(), $"csls-taskport-{Guid.NewGuid():N}.plist");
        try
        {
            string source = new Uri(PolicyPath).AbsoluteUri;
            File.WriteAllText(path, $"<!DOCTYPE plist [<!ENTITY external SYSTEM '{source}'>]>" +
                "<plist><dict><key>class</key><string>&external;</string></dict></plist>");
            Assert.ThrowsExactly<XmlException>(() =>
                MacDebuggerTestAuthorization.CreatePolicy(File.ReadAllText(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string PolicyPath =>
        Path.Join(EditorToolResolver.FindRepositoryRoot(), "test-assets", "debugger-taskport.plist");
}
