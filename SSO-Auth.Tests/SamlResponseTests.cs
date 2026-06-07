using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using Jellyfin.Plugin.SSO_Auth;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

/// <summary>
/// Tests for the SAML <see cref="Response"/> validation: signature checking, validity-window
/// enforcement, signature-wrapping rejection, and the replay-detection helpers.
/// </summary>
public class SamlResponseTests
{
    private static readonly DateTime Past = DateTime.UtcNow.AddHours(-1);
    private static readonly DateTime Future = DateTime.UtcNow.AddHours(1);

    [Fact]
    public void ValidSignedAssertion_IsValid_AndExposesClaims()
    {
        var (certBase64, cert) = NewCert();
        var xml = SignAssertion(BuildResponse("a1", "alice", Future, Past, "user"), "a1", cert);

        var response = Make(xml, certBase64);

        Assert.True(response.IsValid());
        Assert.Equal("alice", response.GetNameID());
        Assert.Equal("a1", response.GetAssertionId());
        Assert.Contains("user", response.GetCustomAttributes("Role"));
    }

    [Fact]
    public void TamperedContentAfterSigning_IsInvalid()
    {
        var (certBase64, cert) = NewCert();
        var xml = SignAssertion(BuildResponse("a1", "alice", Future, Past), "a1", cert);

        // Flip the NameID after the signature was computed.
        var tampered = xml.Replace("<saml:NameID>alice</saml:NameID>", "<saml:NameID>admin</saml:NameID>");

        Assert.False(Make(tampered, certBase64).IsValid());
    }

    [Fact]
    public void SignedWithDifferentCertificate_IsInvalid()
    {
        var (_, signingCert) = NewCert();
        var (otherCertBase64, _) = NewCert();
        var xml = SignAssertion(BuildResponse("a1", "alice", Future, Past), "a1", signingCert);

        Assert.False(Make(xml, otherCertBase64).IsValid());
    }

    [Fact]
    public void UnsignedAssertion_IsInvalid()
    {
        var (certBase64, _) = NewCert();
        var xml = BuildResponse("a1", "alice", Future, Past);

        Assert.False(Make(xml, certBase64).IsValid());
    }

    [Fact]
    public void ExpiredAssertion_IsInvalid()
    {
        var (certBase64, cert) = NewCert();
        var xml = SignAssertion(
            BuildResponse("a1", "alice", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-2)),
            "a1",
            cert);

        Assert.False(Make(xml, certBase64).IsValid());
    }

    [Fact]
    public void MissingNotOnOrAfter_IsInvalid()
    {
        // An assertion with no expiry must be rejected so a captured copy cannot live forever.
        var (certBase64, cert) = NewCert();
        var xml = SignAssertion(BuildResponse("a1", "alice", null, null), "a1", cert);

        Assert.False(Make(xml, certBase64).IsValid());
    }

    [Fact]
    public void NotYetValid_IsInvalid()
    {
        var (certBase64, cert) = NewCert();
        var xml = SignAssertion(
            BuildResponse("a1", "alice", DateTime.UtcNow.AddHours(2), DateTime.UtcNow.AddHours(1)),
            "a1",
            cert);

        Assert.False(Make(xml, certBase64).IsValid());
    }

    [Fact]
    public void RecentlyExpiredWithinClockSkew_IsValid()
    {
        // NotOnOrAfter one minute in the past is tolerated by the 3-minute clock-skew allowance.
        var (certBase64, cert) = NewCert();
        var xml = SignAssertion(
            BuildResponse("a1", "alice", DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddHours(-1)),
            "a1",
            cert);

        Assert.True(Make(xml, certBase64).IsValid());
    }

    [Fact]
    public void SignatureWrapping_ForgedFirstAssertion_IsInvalid()
    {
        // Classic XSW: a validly signed assertion ("real") is wrapped behind a forged first
        // assertion claiming a privileged identity. The cryptographic signature still checks out,
        // but the signed element is no longer the assertion whose data is read, so it must fail.
        var (certBase64, cert) = NewCert();
        var signed = SignAssertion(BuildResponse("real", "attacker", Future, Past), "real", cert);
        var wrapped = InjectForgedFirstAssertion(signed, "admin");

        var response = Make(wrapped, certBase64);

        Assert.Equal("admin", response.GetNameID()); // the data reader sees the forged identity...
        Assert.False(response.IsValid());             // ...so validation must reject it.
    }

    [Fact]
    public void GetExpiry_ReturnsLatestNotOnOrAfterPlusSkew()
    {
        var (certBase64, cert) = NewCert();
        var notOnOrAfter = DateTime.UtcNow.AddHours(1);
        var xml = SignAssertion(BuildResponse("a1", "alice", notOnOrAfter, Past), "a1", cert);

        var expiry = Make(xml, certBase64).GetExpiry();

        // Expiry is the assertion's NotOnOrAfter plus the clock-skew allowance.
        Assert.True(expiry > notOnOrAfter);
        Assert.True(expiry < notOnOrAfter.AddMinutes(10));
    }

    [Fact]
    public void GetAudiences_ReturnsDeclaredAudience()
    {
        var (certBase64, _) = NewCert();
        var xml =
            "<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" xmlns:saml=\"urn:oasis:names:tc:SAML:2.0:assertion\" ID=\"resp1\" Version=\"2.0\">"
            + "<saml:Assertion ID=\"a1\" Version=\"2.0\"><saml:Issuer>https://idp.test</saml:Issuer>"
            + "<saml:Subject><saml:NameID>alice</saml:NameID></saml:Subject>"
            + "<saml:Conditions><saml:AudienceRestriction><saml:Audience>jellyfin-saml</saml:Audience></saml:AudienceRestriction></saml:Conditions>"
            + "</saml:Assertion></samlp:Response>";

        Assert.Contains("jellyfin-saml", Make(xml, certBase64).GetAudiences());
    }

    [Fact]
    public void GetAudiences_EmptyWhenNoRestriction()
    {
        var (certBase64, _) = NewCert();

        Assert.Empty(Make(BuildResponse("a1", "alice", Future, Past), certBase64).GetAudiences());
    }

    // ----- helpers -----

    private static Response Make(string xml, string certBase64) =>
        new Response(certBase64, Convert.ToBase64String(Encoding.UTF8.GetBytes(xml)));

    private static string Fmt(DateTime dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static (string CertBase64, X509Certificate2 Cert) NewCert()
    {
        var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=sso-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (Convert.ToBase64String(cert.Export(X509ContentType.Cert)), cert);
    }

    private static string BuildResponse(string assertionId, string nameId, DateTime? notOnOrAfter, DateTime? notBefore, params string[] roles)
    {
        var sb = new StringBuilder();
        sb.Append("<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" xmlns:saml=\"urn:oasis:names:tc:SAML:2.0:assertion\" ID=\"resp1\" Version=\"2.0\">");
        sb.Append($"<saml:Assertion ID=\"{assertionId}\" Version=\"2.0\">");
        sb.Append("<saml:Issuer>https://idp.test</saml:Issuer>");
        sb.Append("<saml:Subject>");
        sb.Append($"<saml:NameID>{nameId}</saml:NameID>");
        sb.Append("<saml:SubjectConfirmation Method=\"urn:oasis:names:tc:SAML:2.0:cm:bearer\">");
        sb.Append("<saml:SubjectConfirmationData");
        if (notOnOrAfter.HasValue)
        {
            sb.Append($" NotOnOrAfter=\"{Fmt(notOnOrAfter.Value)}\"");
        }

        sb.Append("/>");
        sb.Append("</saml:SubjectConfirmation>");
        sb.Append("</saml:Subject>");
        sb.Append("<saml:Conditions");
        if (notBefore.HasValue)
        {
            sb.Append($" NotBefore=\"{Fmt(notBefore.Value)}\"");
        }

        if (notOnOrAfter.HasValue)
        {
            sb.Append($" NotOnOrAfter=\"{Fmt(notOnOrAfter.Value)}\"");
        }

        sb.Append("/>");
        if (roles is { Length: > 0 })
        {
            sb.Append("<saml:AttributeStatement><saml:Attribute Name=\"Role\">");
            foreach (var role in roles)
            {
                sb.Append($"<saml:AttributeValue>{role}</saml:AttributeValue>");
            }

            sb.Append("</saml:Attribute></saml:AttributeStatement>");
        }

        sb.Append("</saml:Assertion>");
        sb.Append("</samlp:Response>");
        return sb.ToString();
    }

    private static string SignAssertion(string xml, string assertionId, X509Certificate2 cert)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);

        var signedXml = new SignedXml(doc) { SigningKey = cert.GetRSAPrivateKey() };
        signedXml.SignedInfo.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;

        var reference = new Reference("#" + assertionId) { DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(cert));
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();
        var signatureElement = signedXml.GetXml();

        var nsmgr = NamespaceManager(doc);
        var assertion = (XmlElement)doc.SelectSingleNode($"//saml:Assertion[@ID='{assertionId}']", nsmgr);
        var issuer = assertion.SelectSingleNode("saml:Issuer", nsmgr);
        assertion.InsertAfter(doc.ImportNode(signatureElement, true), issuer);

        return doc.OuterXml;
    }

    private static string InjectForgedFirstAssertion(string signedXml, string forgedNameId)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(signedXml);
        var nsmgr = NamespaceManager(doc);

        const string SamlNs = "urn:oasis:names:tc:SAML:2.0:assertion";
        var forged = doc.CreateElement("saml", "Assertion", SamlNs);
        forged.SetAttribute("ID", "forged");
        forged.SetAttribute("Version", "2.0");

        var issuer = doc.CreateElement("saml", "Issuer", SamlNs);
        issuer.InnerText = "https://idp.test";
        forged.AppendChild(issuer);

        var subject = doc.CreateElement("saml", "Subject", SamlNs);
        var nameId = doc.CreateElement("saml", "NameID", SamlNs);
        nameId.InnerText = forgedNameId;
        subject.AppendChild(nameId);
        forged.AppendChild(subject);

        var conditions = doc.CreateElement("saml", "Conditions", SamlNs);
        conditions.SetAttribute("NotOnOrAfter", Fmt(Future));
        forged.AppendChild(conditions);

        var realAssertion = doc.SelectSingleNode("/samlp:Response/saml:Assertion", nsmgr);
        realAssertion.ParentNode.InsertBefore(forged, realAssertion);

        return doc.OuterXml;
    }

    private static XmlNamespaceManager NamespaceManager(XmlDocument doc)
    {
        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("samlp", "urn:oasis:names:tc:SAML:2.0:protocol");
        nsmgr.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");
        return nsmgr;
    }
}
