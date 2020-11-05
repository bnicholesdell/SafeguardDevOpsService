﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using OneIdentity.DevOps.ConfigDb;
using OneIdentity.DevOps.Data;
using OneIdentity.DevOps.Data.Spp;
using OneIdentity.SafeguardDotNet;
using Microsoft.AspNetCore.WebUtilities;
using OneIdentity.DevOps.Common;
using OneIdentity.DevOps.Exceptions;
using OneIdentity.DevOps.Extensions;
using A2ARetrievableAccount = OneIdentity.DevOps.Data.Spp.A2ARetrievableAccount;

namespace OneIdentity.DevOps.Logic
{
    internal class SafeguardLogic : ISafeguardLogic, IDisposable
    {
        private const int DefaultApiVersion = 3;

        private readonly Serilog.ILogger _logger;
        private readonly IConfigurationRepository _configDb;

        private ServiceConfiguration _serviceConfiguration;

        public SafeguardLogic(IConfigurationRepository configDb)
        {
            _configDb = configDb;
            _logger = Serilog.Log.Logger;
        }

        bool CertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return CertificateHelper.CertificateValidation(sender, certificate, chain, sslPolicyErrors, _logger, _configDb);
        }

        private DevOpsException LogAndException(string msg, Exception ex = null)
        {
            _logger.Error(msg);
            return new DevOpsException(msg, ex);
        }

        private SafeguardDevOpsConnection GetSafeguardAppliance(ISafeguardConnection sg)
        {
            try
            {
                var availabilityJson = sg.InvokeMethod(Service.Notification, Method.Get, "Status/Availability");
                var applianceAvailability = JsonHelper.DeserializeObject<ApplianceAvailability>(availabilityJson);

                return new SafeguardDevOpsConnection()
                {
                    ApplianceAddress = _configDb.SafeguardAddress,
                    ApplianceId = applianceAvailability.ApplianceId,
                    ApplianceName = applianceAvailability.ApplianceName,
                    ApplianceVersion = applianceAvailability.ApplianceVersion,
                    ApplianceState = applianceAvailability.ApplianceCurrentState,
                    DevOpsInstanceId = _configDb.SvcId,
                    Version = WellKnownData.DevOpsServiceVersion()
                };

            }
            catch (SafeguardDotNetException ex)
            {
                throw new DevOpsException($"Failed to get the appliance information: {ex.Message}");
            }
        }

        private SafeguardDevOpsConnection GetSafeguardAvailability(ISafeguardConnection sg, SafeguardDevOpsConnection safeguardConnection)
        {
            var safeguard = GetSafeguardAppliance(sg);
            safeguardConnection.ApplianceId = safeguard.ApplianceId;
            safeguardConnection.ApplianceName = safeguard.ApplianceName;
            safeguardConnection.ApplianceVersion = safeguard.ApplianceVersion;
            safeguardConnection.ApplianceState = safeguard.ApplianceState;
            safeguardConnection.DevOpsInstanceId = _configDb.SvcId;
            safeguardConnection.Version = safeguard.Version;

            return safeguardConnection;
        }

        private bool FetchAndStoreSignatureCertificate(string token, SafeguardDevOpsConnection safeguardConnection)
        {
            var signatureCert = FetchSignatureCertificate(token, safeguardConnection.ApplianceAddress);

            if (signatureCert != null)
            {
                if (ValidateLogin(token, safeguardConnection, signatureCert))
                {
                    _configDb.SigningCertificate = signatureCert;
                    return true;
                }
            }

            return false;
        }

        private string FetchSignatureCertificate(string token, string applianceAddress)
        {
            // Chicken and egg problem here. Fetching and storing the signature certificate is the first
            //  thing that has to happen on a new system.  We can't check the SSL certificate unless a certificate
            //  chain has been provided.  However a certificate chain can't be provided until after login but
            //  login can't happen until we have the signature certificate.  So we will just ignore the
            //  the validation of the SSL certificate.
            HttpClientHandler handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback =
                    (httpRequestMessage, cert, certChain, policyErrors) => true
            };

            string result;
            using (var client = new HttpClient(handler))
            {
                client.BaseAddress = new Uri($"https://{applianceAddress}");
                var response = client.GetAsync("RSTS/Saml2FedMetadata").Result;
                response.EnsureSuccessStatusCode();

                result = response.Content.ReadAsStringAsync().Result;
            }

            if (result != null)
            {
                var xml = new XmlDocument();
                xml.LoadXml(result);
                var certificates = xml.DocumentElement.GetElementsByTagName("X509Certificate");
                if (certificates != null && certificates.Count > 0)
                {
                    return certificates.Item(0).InnerText;
                }
            }

            return null;
        }

        private SafeguardDevOpsConnection ConnectAnonymous(string safeguardAddress, int apiVersion, bool ignoreSsl)
        {
            ISafeguardConnection sg = null;
            try
            {
                var safeguardConnection = new SafeguardDevOpsConnection
                {
                    ApplianceAddress = safeguardAddress,
                    IgnoreSsl = _configDb.IgnoreSsl ?? ignoreSsl,
                    ApiVersion = apiVersion
                };
        
                sg = ignoreSsl ? Safeguard.Connect(safeguardAddress, apiVersion, true) :
                    Safeguard.Connect(safeguardAddress, CertificateValidationCallback, apiVersion);
                return GetSafeguardAvailability(sg, safeguardConnection);
            }
            catch (SafeguardDotNetException ex)
            {
                throw LogAndException($"Failed to contact Safeguard at '{safeguardAddress}': {ex.Message}", ex);
            }
            finally
            {
                sg?.Dispose();
            }
        }

        private ISafeguardConnection ConnectWithAccessToken(string token, SafeguardDevOpsConnection safeguardConnection)
        {
            if (_serviceConfiguration != null)
            {
                DisconnectWithAccessToken();
            }

            if (string.IsNullOrEmpty(safeguardConnection.ApplianceAddress))
                throw new DevOpsException("Missing safeguard appliance configuration.");
            if (string.IsNullOrEmpty(token))
                throw new DevOpsException("Missing safeguard access token.");

            _serviceConfiguration = new ServiceConfiguration
            {
                AccessToken = token.ToSecureString()
            };

            return Connect(safeguardConnection.ApplianceAddress, token.ToSecureString(), safeguardConnection.ApiVersion, safeguardConnection.IgnoreSsl);
        }

        private void DisconnectWithAccessToken()
        {
            _serviceConfiguration?.AccessToken?.Dispose();
            _serviceConfiguration = null;
        }

        private bool GetAndValidateUserPermissions(string token, SafeguardDevOpsConnection safeguardConnection)
        {
            ISafeguardConnection sg = null;
            try
            {
                sg = ConnectWithAccessToken(token, safeguardConnection);

                var meJson = sg.InvokeMethod(Service.Core, Method.Get, "Me");
                var loggedInUser = JsonHelper.DeserializeObject<LoggedInUser>(meJson);

                var valid = loggedInUser.AdminRoles.Any(x => x.Equals("ApplianceAdmin") || x.Equals("OperationsAdmin"));
                if (valid)
                {
                    _serviceConfiguration.Appliance = GetSafeguardAvailability(sg, safeguardConnection);

                    AuthorizedCache.Instance.Add(new ServiceConfiguration(loggedInUser)
                    {
                        AccessToken = token.ToSecureString(),
                        Appliance = _serviceConfiguration.Appliance
                    });
                }

                return valid;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get the user information: {ex.Message}", ex);
            }
            finally
            {
                sg?.Dispose();
            }

            return false;
        }

        private void CreateA2AUser(ISafeguardConnection sg)
        {
            string thumbprint = _configDb.UserCertificate?.Thumbprint;

            if (thumbprint == null)
                throw new DevOpsException("Failed to create A2A user due to missing client certificate");

            var a2aUser = GetA2AUser(sg);
            if (a2aUser == null)
            {
                a2aUser = new A2AUser()
                {
                    UserName = WellKnownData.DevOpsRegistrationName(_configDb.SvcId),
                    PrimaryAuthenticationIdentity = thumbprint
                };

                var a2aUserStr = JsonHelper.SerializeObject(a2aUser);
                try
                {
                    var result = sg.InvokeMethodFull(Service.Core, Method.Post, "Users", a2aUserStr);
                    if (result.StatusCode == HttpStatusCode.Created)
                    {
                        a2aUser = JsonHelper.DeserializeObject<A2AUser>(result.Body);
                        _configDb.A2aUserId = a2aUser.Id;
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException($"Failed to create the A2A user: {ex.Message}", ex);
                }
            }
            else
            {
                if (!a2aUser.PrimaryAuthenticationIdentity.Equals(thumbprint, StringComparison.InvariantCultureIgnoreCase))
                {
                    try {
                        a2aUser.PrimaryAuthenticationIdentity = thumbprint;
                        var a2aUserStr = JsonHelper.SerializeObject(a2aUser);
                        sg.InvokeMethodFull(Service.Core, Method.Put, $"Users/{a2aUser.Id}", a2aUserStr);
                    }
                    catch (Exception ex)
                    {
                        throw LogAndException($"Failed to update the A2A user: {ex.Message}", ex);
                    }
                }
            }
        }

        private A2AUser GetA2AUser(ISafeguardConnection sg)
        {
            FullResponse result;

            // If we don't have a user Id then try to find the user by name
            if (_configDb.A2aUserId == null)
            {
                var p = new Dictionary<string, string> {{"filter", $"UserName eq '{WellKnownData.DevOpsUserName(_configDb.SvcId)}'"}};

                try
                {
                    result = sg.InvokeMethodFull(Service.Core, Method.Get, "Users", null, p);
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        var foundUsers = JsonHelper.DeserializeObject<List<A2AUser>>(result.Body);

                        if (foundUsers.Count > 0)
                        {
                            var a2aUser = foundUsers.FirstOrDefault();
                            _configDb.A2aUserId = a2aUser.Id;
                            return a2aUser;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to get the A2A user by name: {ex.Message}");
                }
            }
            else // Otherwise just get the user by id
            {
                try
                {
                    result = sg.InvokeMethodFull(Service.Core, Method.Get, $"Users/{_configDb.A2aUserId}");
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        return JsonHelper.DeserializeObject<A2AUser>(result.Body);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to get the A2A user by id {_configDb.A2aUserId}: {ex.Message}");
                }

                // Apparently the user id we have is wrong so get rid of it.
                _configDb.A2aUserId = null;
            }

            return null;
        }

        private void EnableA2AService(ISafeguardConnection sg)
        {
            try
            {
                var result = sg.InvokeMethodFull(Service.Appliance, Method.Post, "A2AService/Enable");
                if (result.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Error("Failed to start the A2A service.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start the A2A service: {ex.Message}");
            }
        }

        private void CreateA2ARegistration(ISafeguardConnection sg, A2ARegistrationType registrationType)
        {
            if (_configDb.A2aUserId == null)
                throw new DevOpsException("Failed to create A2A registration due to missing A2A user");

            var a2aRegistration = GetA2ARegistration(sg, registrationType);
            if (a2aRegistration == null)
            {
                var registration = new A2ARegistration()
                {
                    AppName = registrationType == A2ARegistrationType.Account ?
                        WellKnownData.DevOpsRegistrationName(_configDb.SvcId) : WellKnownData.DevOpsVaultRegistrationName(_configDb.SvcId),
                    CertificateUserId = _configDb.A2aUserId.Value,
                    VisibleToCertificateUsers = true
                };

                var registrationStr = JsonHelper.SerializeObject(registration);

                try
                {
                    var result = sg.InvokeMethodFull(Service.Core, Method.Post, "A2ARegistrations", registrationStr);
                    if (result.StatusCode == HttpStatusCode.Created)
                    {
                        registration = JsonHelper.DeserializeObject<A2ARegistration>(result.Body);
                        if (registrationType == A2ARegistrationType.Account)
                        {
                            _configDb.A2aRegistrationId = registration.Id;
                        }
                        else
                        {
                            _configDb.A2aVaultRegistrationId = registration.Id;
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException($"Failed to create the A2A registration: {ex.Message}", ex);
                }
            }
        }

        private A2ARegistration GetA2ARegistration(ISafeguardConnection sg, A2ARegistrationType registrationType)
        {
            FullResponse result;

            // If we don't have a registration Id then try to find the registration by name
            if ((registrationType == A2ARegistrationType.Account && _configDb.A2aRegistrationId == null) ||
                (registrationType == A2ARegistrationType.Vault && _configDb.A2aVaultRegistrationId == null))
            {
                var knownRegistrationName = (registrationType == A2ARegistrationType.Account)
                    ? WellKnownData.DevOpsRegistrationName(_configDb.SvcId)
                    : WellKnownData.DevOpsVaultRegistrationName(_configDb.SvcId);
                try
                {
                    var p = new Dictionary<string, string>
                        {{"filter", $"AppName eq '{knownRegistrationName}'"}};

                    result = sg.InvokeMethodFull(Service.Core, Method.Get, "A2ARegistrations", null, p);
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        var foundRegistrations = JsonHelper.DeserializeObject<List<A2ARegistration>>(result.Body);

                        if (foundRegistrations.Count > 0)
                        {
                            var registration = foundRegistrations.FirstOrDefault();
                            if (registration != null)
                            {
                                if (registrationType == A2ARegistrationType.Account)
                                {
                                    _configDb.A2aRegistrationId = registration.Id;
                                }
                                else
                                {
                                    _configDb.A2aVaultRegistrationId = registration.Id;
                                }
                            }

                            return registration;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to get the A2A user by name {knownRegistrationName}: {ex.Message}");
                }
            }
            else // Otherwise just get the registration by id
            {
                var registrationId = (registrationType == A2ARegistrationType.Account)
                    ? _configDb.A2aRegistrationId
                    : _configDb.A2aVaultRegistrationId;
                try
                {
                    result = sg.InvokeMethodFull(Service.Core, Method.Get,
                        $"A2ARegistrations/{registrationId}");
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        return JsonHelper.DeserializeObject<A2ARegistration>(result.Body);
                    }
                }
                catch (Exception ex)
                {
                    if (ex is SafeguardDotNetException && ((SafeguardDotNetException)ex).HttpStatusCode == HttpStatusCode.NotFound)
                    {
                        _logger.Error($"Registration not found for id '{registrationId}': {ex.Message}");
                    }
                    else
                    {
                        throw LogAndException($"Failed to get the registration for id '{registrationId}': {ex.Message}", ex);
                    }
                }
            }

            // Apparently the registration id we have is wrong so get rid of it.
            if (registrationType == A2ARegistrationType.Account)
            {
                _configDb.A2aRegistrationId = null;
            }
            else
            {
                _configDb.A2aVaultRegistrationId = null;
            }

            return null;
        }

        private string LocalIPAddress()
        {
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() || WellKnownData.IsLinux)
            {
                return null;
            }

            var host = Dns.GetHostEntry(Dns.GetHostName());

            var addresses = host.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork);

            return string.Join(",", addresses.Select(x => $"\"{x}\""));
        }

        public void RemoveClientCertificate()
        {
            _configDb.UserCertificateBase64Data = null;
            _configDb.UserCertificatePassphrase = null;
            _configDb.UserCertificateThumbprint = null;
        }

        public void RemoveWebServerCertificate()
        {
            _configDb.WebSslCertificateBase64Data = null;
            _configDb.WebSslCertificatePassphrase = null;
            _configDb.WebSslCertificate = CertificateHelper.CreateDefaultSslCertificate();
        }

        public bool IsLoggedIn()
        {
            return _serviceConfiguration != null;
        }

        public ISafeguardConnection Connect()
        {
            if (!IsLoggedIn())
                throw new DevOpsException("Not logged in");

            return Connect(_serviceConfiguration.Appliance.ApplianceAddress,
                _serviceConfiguration.AccessToken,
                _serviceConfiguration.Appliance.ApiVersion,
                _serviceConfiguration.Appliance.IgnoreSsl);
        }

        private ISafeguardConnection Connect(string address, SecureString token, int? version, bool? ignoreSsl)
        {
            if (!IsLoggedIn())
            {
                throw new DevOpsException("Not logged in");
            }

            try
            {
                _logger.Debug("Connecting to Safeguard: {address}");
                return (ignoreSsl.HasValue && ignoreSsl.Value) ?
                    Safeguard.Connect(address, token, version ?? DefaultApiVersion, true) :
                    Safeguard.Connect(address, token, CertificateValidationCallback, version ?? DefaultApiVersion);
            }
            catch (SafeguardDotNetException ex)
            {
                throw LogAndException($"Failed to connect to Safeguard at '{address}': {ex.Message}", ex);
            }
        }

        public bool ValidateLogin(string token, bool tokenOnly = false)
        {

            return ValidateLogin(token, tokenOnly ? null : new SafeguardDevOpsConnection()
            {
                ApiVersion = _configDb.ApiVersion ?? DefaultApiVersion,
                ApplianceAddress = _configDb.SafeguardAddress,
                IgnoreSsl = _configDb.IgnoreSsl ?? false
            });
        }

        private bool ValidateLogin(string token, SafeguardDevOpsConnection safeguardConnection = null, string tempKey = null)
        {
            if (token == null)
                return false;

            try
            {
                var key = tempKey ?? _configDb.SigningCertificate;
                var bytes = Convert.FromBase64String(key);
                var cert = new X509Certificate2(bytes);

                var parts = token.Split('.');
                var header = parts[0];
                var payload = parts[1];
                var signature = Base64UrlTextEncoder.Decode(parts[2]);

                var data = Encoding.UTF8.GetBytes(header + '.' + payload);

                var validToken = cert.GetRSAPublicKey()
                    .VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                if (validToken && safeguardConnection != null)
                {
                    return GetAndValidateUserPermissions(token, safeguardConnection);
                }

                return validToken;
            } catch { }

            return false;
        }

        public CertificateInfo GetCertificateInfo(CertificateType certificateType)
        {
            var cert = certificateType == CertificateType.A2AClient ? _configDb.UserCertificate : _configDb.WebSslCertificate;

            if (cert != null)
            {
                var result = new CertificateInfo()
                {
                    Thumbprint = cert.Thumbprint,
                    IssuedBy = cert.Issuer,
                    Subject = cert.Subject,
                    NotAfter = cert.NotBefore,
                    NotBefore = cert.NotAfter,
                    Base64CertificateData = cert.ToPemFormat()
                };
                return result;
            }

            var msg = $"{Enum.GetName(typeof(CertificateType), certificateType)} certificate not found.";
            _logger.Error(msg);
            throw new DevOpsException(msg, HttpStatusCode.NotFound);
        }

        public void InstallCertificate(CertificateInfo certificate, CertificateType certificateType)
        {
            var certificateBytes = CertificateHelper.ConvertPemToData(certificate.Base64CertificateData);
            X509Certificate2 cert;
            try
            {
                cert = certificate.Passphrase == null
                    ? new X509Certificate2(certificateBytes)
                    : new X509Certificate2(certificateBytes, certificate.Passphrase);
                _logger.Debug($"Parsed certificate for installation: subject={cert.SubjectName.Name}, thumbprint={cert.Thumbprint}");
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to convert the provided certificate: {ex.Message}", ex);
            }

            if (cert.HasPrivateKey)
            {
                _logger.Debug($"Parsed certificate contains private key");
                if (!CertificateHelper.ValidateCertificate(cert, certificateType))
                    throw new DevOpsException("Invalid certificate");

                switch (certificateType)
                {
                    case CertificateType.A2AClient:
                    {
                        _configDb.UserCertificatePassphrase = certificate.Passphrase;
                        _configDb.UserCertificateBase64Data = certificate.Base64CertificateData;
                        break;
                    }
                    case CertificateType.WebSsl:
                    {
                        _configDb.WebSslCertificatePassphrase = certificate.Passphrase;
                        _configDb.WebSslCertificateBase64Data = certificate.Base64CertificateData;
                        break;
                    }
                    default:
                    {
                        throw new DevOpsException("Invalid certificate type");
                    }
                }
            }
            else
            {
                try
                {
                    using var rsa = RSA.Create();
                    var privateKeyBytesBase64 = certificateType == CertificateType.A2AClient ? 
                        _configDb.UserCsrPrivateKeyBase64Data : _configDb.WebSslCsrPrivateKeyBase64Data;
                    if (privateKeyBytesBase64 == null)
                    {
                        throw LogAndException("Failed to find a matching private key. Possibly a mismatched CSR type was selected.");
                    }
                    var privateKeyBytes = Convert.FromBase64String(privateKeyBytesBase64);
                    rsa.ImportRSAPrivateKey(privateKeyBytes, out _);

                    using (X509Certificate2 pubOnly = cert)
                    using (X509Certificate2 pubPrivEphemeral = pubOnly.CopyWithPrivateKey(rsa))
                    {
                        if (!CertificateHelper.ValidateCertificate(pubPrivEphemeral, certificateType))
                            throw new DevOpsException("Invalid certificate");

                        switch (certificateType)
                        {
                            case CertificateType.A2AClient:
                            {
                                _configDb.UserCertificatePassphrase = null;
                                _configDb.UserCertificateBase64Data = Convert.ToBase64String(pubPrivEphemeral.Export(X509ContentType.Pfx));
                                break;
                            }
                            case CertificateType.WebSsl:
                            {
                                _configDb.WebSslCertificatePassphrase = null;
                                _configDb.WebSslCertificateBase64Data = Convert.ToBase64String(pubPrivEphemeral.Export(X509ContentType.Pfx));
                                break;
                            }
                            default:
                            {
                                throw new DevOpsException("Invalid certificate type");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw LogAndException($"Failed to import the certificate: {ex.Message}", ex);
                }
            }
        }

        public string GetCSR(int? size, string subjectName, string sanDns, string sanIp, CertificateType certificateType)
        {
            var certSize = 2048;
            var certSubjectName = certificateType == CertificateType.A2AClient ?
                WellKnownData.DevOpsServiceClientCertificate(_configDb.SvcId) : WellKnownData.DevOpsServiceWebSslCertificate(_configDb.SvcId);

            if (size != null)
                certSize = size.Value;
            if (subjectName != null)
            {
                try
                {
                    subjectName = subjectName.Trim('"').Trim('\'');
                    var validCnName = new X500DistinguishedName(subjectName);
                    certSubjectName = validCnName.Name;
                }
                catch (Exception ex)
                {
                    throw LogAndException("Invalid subject name.", ex);
                }
            }

            using (RSA rsa = RSA.Create(certSize))
            {
                var certificateRequest = new CertificateRequest(certSubjectName, rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                certificateRequest.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));

                switch (certificateType)
                {
                    case CertificateType.A2AClient:
                    {
                        certificateRequest.CertificateExtensions.Add(
                            new X509KeyUsageExtension(
                                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement | X509KeyUsageFlags.KeyEncipherment,
                                true));
                        certificateRequest.CertificateExtensions.Add(
                            new X509EnhancedKeyUsageExtension(
                                new OidCollection
                                {
                                    new Oid("1.3.6.1.5.5.7.3.2")
                                },
                                false));
                        break;
                    }
                    case CertificateType.WebSsl:
                    {
                        certificateRequest.CertificateExtensions.Add(
                            new X509KeyUsageExtension(
                                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement | X509KeyUsageFlags.KeyEncipherment,
                                true));
                        certificateRequest.CertificateExtensions.Add(
                            new X509EnhancedKeyUsageExtension(
                                new OidCollection
                                {
                                    new Oid("1.3.6.1.5.5.7.3.1")
                                },
                                false));
                        break;
                    }
                    default:
                    {
                        throw new DevOpsException("Invalid certificate type");
                    }
                }

                certificateRequest.CertificateExtensions.Add(
                    new X509SubjectKeyIdentifierExtension(certificateRequest.PublicKey, false));

                var sanBuilder = new SubjectAlternativeNameBuilder();
                if (sanIp != null)
                {
                    try
                    {
                        var addresses = sanIp.Split(',').ToList();
                        addresses.ForEach(x => sanBuilder.AddIpAddress(IPAddress.Parse(x)));
                    }
                    catch (Exception ex)
                    {
                        throw LogAndException("Invalid SAN IP address list.", ex);
                    }
                }

                if (sanDns != null)
                {
                    try
                    {
                        var dnsNames = sanDns.Split(',').ToList();
                        dnsNames.ForEach(x => sanBuilder.AddDnsName(x.Trim()));
                    }
                    catch (Exception ex)
                    {
                        throw LogAndException("Invalid SAN DNS list.", ex);
                    }
                }
                certificateRequest.CertificateExtensions.Add(sanBuilder.Build());

                var csr = certificateRequest.CreateSigningRequest();
                switch (certificateType)
                {
                    case CertificateType.A2AClient:
                    {
                        _configDb.UserCsrBase64Data = Convert.ToBase64String(csr);
                        _configDb.UserCsrPrivateKeyBase64Data = Convert.ToBase64String(rsa.ExportRSAPrivateKey());
                        break;
                    }
                    case CertificateType.WebSsl:
                    {
                        _configDb.WebSslCsrBase64Data = Convert.ToBase64String(csr);
                        _configDb.WebSslCsrPrivateKeyBase64Data = Convert.ToBase64String(rsa.ExportRSAPrivateKey());
                        break;
                    }
                    default:
                    {
                        throw new DevOpsException("Invalid certificate type");
                    }
                }

                var builder = new StringBuilder();
                builder.Append("-----BEGIN CERTIFICATE REQUEST-----\r\n");
                builder.Append(PemExtensions.AddPemLineBreaks(Convert.ToBase64String(csr)));
                builder.Append("\r\n-----END CERTIFICATE REQUEST-----");

                return builder.ToString();
            }
        }

        public ServiceConfiguration ConfigureDevOpsService()
        {
            using var sg = Connect();
            CreateA2AUser(sg);
            CreateA2ARegistration(sg, A2ARegistrationType.Account);
            CreateA2ARegistration(sg, A2ARegistrationType.Vault);
            EnableA2AService(sg);

            return GetDevOpsConfiguration();
        }

        public SafeguardDevOpsConnection GetAnonymousSafeguardConnection()
        {
            if (string.IsNullOrEmpty(_configDb.SafeguardAddress))
                return new SafeguardDevOpsConnection();

            ISafeguardConnection sg = null;
            try
            {
                var safeguardConnection = new SafeguardDevOpsConnection
                {
                    ApplianceAddress = _configDb.SafeguardAddress,
                    IgnoreSsl = _configDb.IgnoreSsl,
                    ApiVersion = _configDb.ApiVersion ?? DefaultApiVersion
                };
        
                sg = Safeguard.Connect(safeguardConnection.ApplianceAddress, safeguardConnection.ApiVersion ?? DefaultApiVersion, true);
                return GetSafeguardAvailability(sg, safeguardConnection);
            }
            catch (SafeguardDotNetException ex)
            {
                throw LogAndException($"Failed to contact Safeguard at '{_configDb.SafeguardAddress}': {ex.Message}", ex);
            }
            finally
            {
                sg?.Dispose();
            }
        }

        public SafeguardDevOpsConnection GetSafeguardConnection()
        {
            if (string.IsNullOrEmpty(_configDb.SafeguardAddress))
                return new SafeguardDevOpsConnection();

            return ConnectAnonymous(_configDb.SafeguardAddress, _configDb.ApiVersion ?? DefaultApiVersion, true);
        }

        public SafeguardDevOpsConnection SetSafeguardData(string token, SafeguardData safeguardData)
        {
            if (token == null)
                throw new DevOpsException("Invalid authorization token.", HttpStatusCode.Unauthorized);

            if (_configDb.SafeguardAddress != null)
            {
                var signatureCert = FetchSignatureCertificate(token, _configDb.SafeguardAddress);
                if (!ValidateLogin(token, null, signatureCert))
                {
                    if (_configDb.SafeguardAddress.Equals(safeguardData.ApplianceAddress))
                    {
                        throw new DevOpsException("Authorization Failed: Invalid token", HttpStatusCode.Unauthorized);
                    }

                    throw LogAndException("Invalid token. The previously configured Secrets Broker cannot be repurposed until the configuration is deleted.");
                }
            }

            if (safeguardData.IgnoreSsl.HasValue && !safeguardData.IgnoreSsl.Value  && !_configDb.GetAllTrustedCertificates().Any())
            {
                throw LogAndException("Cannot ignore SSL before adding trusted certificates.");
            }

            var safeguardConnection = ConnectAnonymous(safeguardData.ApplianceAddress,
                safeguardData.ApiVersion ?? DefaultApiVersion, safeguardData.IgnoreSsl ?? false);

            if (safeguardConnection != null && FetchAndStoreSignatureCertificate(token, safeguardConnection))
            {
                _configDb.SafeguardAddress = safeguardData.ApplianceAddress;
                _configDb.ApiVersion = safeguardData.ApiVersion ?? DefaultApiVersion;
                _configDb.IgnoreSsl = safeguardData.IgnoreSsl ?? false;

                safeguardConnection.ApplianceAddress = _configDb.SafeguardAddress;
                safeguardConnection.ApiVersion = _configDb.ApiVersion;
                safeguardConnection.IgnoreSsl = _configDb.IgnoreSsl;
                return safeguardConnection;
            }

            throw LogAndException($"Invalid authorization token or SPP appliance {safeguardData.ApplianceAddress} is unavailable.");
        }

        public void DeleteSafeguardData()
        {
            _configDb.DropDatabase();
            RestartService();
        }

        public void DeleteDevOpsConfiguration()
        {
            DeleteA2ARegistration(A2ARegistrationType.Account);
            DeleteA2ARegistration(A2ARegistrationType.Vault);
            RemoveClientCertificate();
            DeleteSafeguardData();
        }

        public void RestartService()
        {
            // Sleep just for a second to give the caller time to respond before we exit.
            Thread.Sleep(1000);
            Task.Run(() => Environment.Exit(54));
        }

        public IEnumerable<CertificateInfo> GetTrustedCertificates()
        {
            var trustedCertificates = _configDb.GetAllTrustedCertificates().ToArray();
            if (!trustedCertificates.Any())
                return new List<CertificateInfo>();

            return trustedCertificates.Select(x => x.GetCertificateInfo());
        }

        public CertificateInfo GetTrustedCertificate(string thumbPrint)
        {
            if (string.IsNullOrEmpty(thumbPrint))
                throw LogAndException("Invalid thumbprint");

            var certificate = _configDb.GetTrustedCertificateByThumbPrint(thumbPrint);

            return certificate?.GetCertificateInfo();
        }

        private CertificateInfo AddTrustedCertificate(string base64CertificateData)
        {
            if (base64CertificateData == null)
                throw LogAndException("Certificate cannot be null");

            try
            {
                var certificateBytes = CertificateHelper.ConvertPemToData(base64CertificateData);
                var cert = new X509Certificate2(certificateBytes);
                _logger.Debug($"Parsed new trusted certificate: subject={cert.SubjectName}, thumbprint={cert.Thumbprint}.");

                // Check of the certificate already exists and just return it if it does.
                var existingCert = _configDb.GetTrustedCertificateByThumbPrint(cert.Thumbprint);
                if (existingCert != null)
                {
                    _logger.Debug($"New trusted certificate already exists.");
                    return existingCert.GetCertificateInfo();
                }

                if (!CertificateHelper.ValidateCertificate(cert, CertificateType.Trusted))
                    throw new DevOpsException("Invalid certificate");

                var trustedCertificate = new TrustedCertificate()
                {
                    Thumbprint = cert.Thumbprint,
                    Base64CertificateData = cert.ToPemFormat()
                };

                _configDb.SaveTrustedCertificate(trustedCertificate);
                return trustedCertificate.GetCertificateInfo();
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to add the certificate: {ex.Message}", ex);
            }
        }

        public CertificateInfo AddTrustedCertificate(CertificateInfo certificate)
        {
            return AddTrustedCertificate(certificate.Base64CertificateData);
        }

        public void DeleteTrustedCertificate(string thumbPrint)
        {
            if (string.IsNullOrEmpty(thumbPrint))
                throw LogAndException("Invalid thumbprint");

            _configDb.DeleteTrustedCertificateByThumbPrint(thumbPrint);
        }

        public IEnumerable<CertificateInfo> ImportTrustedCertificates()
        {
            using var sg = Connect();

            IEnumerable<ServerCertificate> serverCertificates = null;
            var trustedCertificates = new List<CertificateInfo>();

            try
            {

                var result = sg.InvokeMethodFull(Service.Core, Method.Get, "TrustedCertificates");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    serverCertificates = JsonHelper.DeserializeObject<IEnumerable<ServerCertificate>>(result.Body);
                    _logger.Debug($"Received {serverCertificates.Count()} certificates from Safeguard.");
                }
            }
            catch (Exception ex)
            {
                throw LogAndException("Failed to get the Safeguard trusted certificates.", ex);
            }

            if (serverCertificates != null)
            {
                foreach (var cert in serverCertificates)
                {
                    try
                    {
                        _logger.Debug($"Importing trusted certificate {cert.Subject} {cert.Thumbprint}.");
                        var certificateInfo = AddTrustedCertificate(cert.Base64CertificateData);
                        trustedCertificates.Add(certificateInfo);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to import certificate {cert.Subject} {cert.Thumbprint} from Safeguard. {ex.Message}");
                    }
                }
            }

            return trustedCertificates;
        }

        public void DeleteAllTrustedCertificates()
        {
            _configDb.DeleteAllTrustedCertificates();
        }

        public object GetAvailableAccounts(string filter, int? page, bool? count, int? limit, string orderby, string q)
        {
            using var sg = Connect();

            try
            {
                var p = new Dictionary<string, string>();
                JsonHelper.AddQueryParameter(p,nameof(filter), filter);
                JsonHelper.AddQueryParameter(p,nameof(page), page?.ToString());
                JsonHelper.AddQueryParameter(p,nameof(limit), limit?.ToString());
                JsonHelper.AddQueryParameter(p,nameof(count), count?.ToString());
                JsonHelper.AddQueryParameter(p,nameof(orderby), orderby);
                JsonHelper.AddQueryParameter(p,nameof(q), q);

                var result = sg.InvokeMethodFull(Service.Core, Method.Get, "PolicyAccounts", null, p);
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    if (count == null || count.Value == false)
                    {
                        var accounts = JsonHelper.DeserializeObject<IEnumerable<SppAccount>>(result.Body);
                        if (accounts != null)
                        {
                            return accounts;
                        }
                    }
                    else
                    {
                        return result.Body;
                    }
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Get available accounts failed. {ex.Message}", ex);
            }

            return new List<SppAccount>();
        }

        public AssetAccount GetAccount(int id)
        {
            using var sg = Connect();

            try
            {
                var result = sg.InvokeMethodFull(Service.Core, Method.Get, $"AssetAccounts/{id}");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    var account = JsonHelper.DeserializeObject<AssetAccount>(result.Body);
                    if (account != null)
                    {
                        return account;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is SafeguardDotNetException && ((SafeguardDotNetException)ex).HttpStatusCode == HttpStatusCode.NotFound)
                {
                    _logger.Error($"Account not found for id '{id}': {ex.Message}");
                }
                else
                {
                    throw LogAndException($"Failed to get the account for id '{id}': {ex.Message}", ex);
                }
            }

            return null;
        }

        public A2ARegistration GetA2ARegistration(A2ARegistrationType registrationType)
        {
            using var sg = Connect();

            return GetA2ARegistration(sg, registrationType);
        }

        public void DeleteA2ARegistration(A2ARegistrationType registrationType)
        {
            if ((registrationType == A2ARegistrationType.Account && _configDb.A2aRegistrationId == null) ||
                (registrationType == A2ARegistrationType.Vault && _configDb.A2aVaultRegistrationId == null))
            {
                var msg = "A2A registration not configured. Skipping...";
                _logger.Error(msg);
                return;
            }

            using var sg = Connect();

            A2ARegistration registration = null;
            try
            {
                registration = GetA2ARegistration(sg, registrationType);
                if (registration != null)
                {
                    var result = sg.InvokeMethodFull(Service.Core, Method.Delete,
                        $"A2ARegistrations/{registration.Id}");
                    if (registrationType == A2ARegistrationType.Account)
                    {
                        _configDb.DeleteAccountMappings();
                        _configDb.A2aRegistrationId = null;
                        _serviceConfiguration.A2ARegistrationName = null;
                    }
                    else
                    {
                        _configDb.A2aVaultRegistrationId = null;
                        _serviceConfiguration.A2AVaultRegistrationName = null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to delete the registration {_configDb.A2aRegistrationId} - {registration?.AppName}: {ex.Message}");
            }

            // Only delete the A2A user when both A2A registrations have been deleted.
            if (_configDb.A2aRegistrationId == null && _configDb.A2aVaultRegistrationId == null)
            {
                A2AUser user = null;
                try
                {
                    user = GetA2AUser(sg);
                    if (user != null)
                    {
                        var result = sg.InvokeMethodFull(Service.Core, Method.Delete, $"Users/{user.Id}");
                        _configDb.DeleteAccountMappings();
                        _serviceConfiguration.UserName = null;
                        _serviceConfiguration.UserDisplayName = null;
                        _serviceConfiguration.IdentityProviderName = null;
                        _serviceConfiguration.AdminRoles = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(
                        $"Failed to delete the A2A certificate user {_configDb.A2aUserId} - {user?.UserName}: {ex.Message}");
                }
            }
        }

        public A2ARetrievableAccount GetA2ARetrievableAccount(int id, A2ARegistrationType registrationType)
        {
            if ((registrationType == A2ARegistrationType.Account && _configDb.A2aRegistrationId == null) ||
                (registrationType == A2ARegistrationType.Vault && _configDb.A2aVaultRegistrationId == null))
            {
                throw LogAndException("A2A registration not configured");
            }

            using var sg = Connect();
            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            try
            {
                var result = sg.InvokeMethodFull(Service.Core, Method.Get, $"A2ARegistrations/{registrationId}/RetrievableAccounts/{id}");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    return JsonHelper.DeserializeObject<A2ARetrievableAccount>(result.Body);
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Get retrievable account failed for account {id}", ex);
            }

            return null;
        }

        public void DeleteA2ARetrievableAccount(int id, A2ARegistrationType registrationType)
        {
            if ((registrationType == A2ARegistrationType.Account && _configDb.A2aRegistrationId == null) ||
                (registrationType == A2ARegistrationType.Vault && _configDb.A2aVaultRegistrationId == null))
            {
                throw LogAndException("A2A registration not configured");
            }

            using var sg = Connect();
            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            try
            {
                var result = sg.InvokeMethodFull(Service.Core, Method.Delete, $"A2ARegistrations/{registrationId}/RetrievableAccounts/{id}");
                if (result.StatusCode != HttpStatusCode.NoContent)
                {
                    _logger.Error($"(Failed to remove A2A retrievable account {id}");
                }
            }
            catch (Exception ex)
            {
                throw LogAndException($"Failed to remove A2A retrievable account {id}", ex);
            }
        }

        public IEnumerable<A2ARetrievableAccount> GetA2ARetrievableAccounts(A2ARegistrationType registrationType)
        {
            if ((registrationType == A2ARegistrationType.Account && _configDb.A2aRegistrationId == null) ||
                (registrationType == A2ARegistrationType.Vault && _configDb.A2aVaultRegistrationId == null))
            {
                throw LogAndException("A2A registration not configured");
            }

            using var sg = Connect();
            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            try
            {
                var result = sg.InvokeMethodFull(Service.Core, Method.Get, $"A2ARegistrations/{registrationId}/RetrievableAccounts");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    return JsonHelper.DeserializeObject<IEnumerable<A2ARetrievableAccount>>(result.Body);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Get retrievable accounts failed: {ex.Message}");
            }

            return new List<A2ARetrievableAccount>();
        }

        public A2ARetrievableAccount GetA2ARetrievableAccountById(A2ARegistrationType registrationType, int accountId)
        {
            if ((registrationType == A2ARegistrationType.Account && _configDb.A2aRegistrationId == null) ||
                (registrationType == A2ARegistrationType.Vault && _configDb.A2aVaultRegistrationId == null))
            {
                throw LogAndException("A2A registration not configured");
            }

            using var sg = Connect();
            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            try
            {
                var result = sg.InvokeMethodFull(Service.Core, Method.Get,
                    $"A2ARegistrations/{registrationId}/RetrievableAccounts/{accountId}");
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    return JsonHelper.DeserializeObject<A2ARetrievableAccount>(result.Body);
                }
            }
            catch (SafeguardDotNetException ex)
            {
                if (ex.HttpStatusCode == HttpStatusCode.NotFound)
                {
                    _logger.Error($"Retrievable account {accountId} not found: {ex.Message}");
                    return null;
                }
                throw LogAndException($"Get retrievable account failed for account id {accountId}", ex);
            }
            catch (Exception ex)
            {
                throw LogAndException($"Get retrievable account failed for account id {accountId}", ex);
            }

            return null;
        }

        public IEnumerable<A2ARetrievableAccount> AddA2ARetrievableAccounts(IEnumerable<SppAccount> accounts, A2ARegistrationType registrationType)
        {
            if (_configDb.A2aRegistrationId == null)
            {
                throw LogAndException("A2A registration not configured");
            }

            using var sg = Connect();
            var ipRestrictions = LocalIPAddress();
            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            foreach (var account in accounts)
            {
                try
                {
                    sg.InvokeMethodFull(Service.Core, Method.Post, $"A2ARegistrations/{registrationId}/RetrievableAccounts",
                        $"{{\"AccountId\":{account.Id}, \"IpRestrictions\":[{ipRestrictions}]}}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to add account {account.Id} - {account.Name}: {ex.Message}");
                }
            }

            return GetA2ARetrievableAccounts(registrationType);
        }

        public void RemoveA2ARetrievableAccounts(IEnumerable<A2ARetrievableAccount> accounts, A2ARegistrationType registrationType)
        {
            if (_configDb.A2aRegistrationId == null)
            {
                throw LogAndException("A2A registration not configured");
            }

            var retrievableAccounts = accounts.ToArray();
            if (retrievableAccounts.All(x => x.AccountId == 0))
            {
                var msg = "Invalid list of accounts. Expecting a list of retrievable accounts.";
                _logger.Error(msg);
                throw new DevOpsException(msg);
            }

            using var sg = Connect();
            var registrationId = (registrationType == A2ARegistrationType.Account)
                ? _configDb.A2aRegistrationId
                : _configDb.A2aVaultRegistrationId;

            foreach (var account in retrievableAccounts)
            {
                try
                {
                    sg.InvokeMethodFull(Service.Core, Method.Delete, $"A2ARegistrations/{registrationId}/RetrievableAccounts/{account.AccountId}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to remove account {account.AccountId} - {account.AccountName}: {ex.Message}");
                }
            }
        }

        public ServiceConfiguration GetDevOpsConfiguration()
        {
            _serviceConfiguration.IdentityProviderName = null;
            _serviceConfiguration.UserName = null;
            _serviceConfiguration.UserDisplayName = null;
            _serviceConfiguration.AdminRoles = null;
            _serviceConfiguration.A2ARegistrationName = null;
            _serviceConfiguration.A2AVaultRegistrationName = null;
            _serviceConfiguration.Thumbprint = null;

            using var sg = Connect();
            var a2aUser = GetA2AUser(sg);
            if (a2aUser != null)
            {
                _serviceConfiguration.IdentityProviderName = a2aUser.IdentityProviderName;
                _serviceConfiguration.UserName = a2aUser.UserName;
                _serviceConfiguration.UserDisplayName = a2aUser.DisplayName;
                _serviceConfiguration.AdminRoles = a2aUser.AdminRoles;
            }

            _serviceConfiguration.Appliance = GetSafeguardAvailability(sg,
                new SafeguardDevOpsConnection()
                {
                    ApplianceAddress = _configDb.SafeguardAddress,
                    IgnoreSsl = _configDb.IgnoreSsl != null && _configDb.IgnoreSsl.Value,
                    ApiVersion = _configDb.ApiVersion ?? DefaultApiVersion
                });

            var a2aRegistration = GetA2ARegistration(sg, A2ARegistrationType.Account);
            if (a2aRegistration != null)
            {
                _serviceConfiguration.A2ARegistrationName = a2aRegistration.AppName;
            }
            a2aRegistration = GetA2ARegistration(sg, A2ARegistrationType.Vault);
            if (a2aRegistration != null)
            {
                _serviceConfiguration.A2AVaultRegistrationName = a2aRegistration.AppName;
            }

            _serviceConfiguration.Thumbprint = _configDb.UserCertificate?.Thumbprint;

            return _serviceConfiguration;
        }

        public void Dispose()
        {
            DisconnectWithAccessToken();
        }
    }
}
