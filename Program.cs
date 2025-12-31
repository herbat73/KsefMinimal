using System.Globalization;
using System.Xml.Linq;
using KSeF.Client.Api.Services;
using KSeF.Client.Api.Services.Internal;
using KSeF.Client.Clients;
using KSeF.Client.Core.Interfaces;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models.Authorization;
using KSeF.Client.Core.Models.Invoices;
using KSeF.Client.Core.Models.Sessions.OnlineSession;
using KSeF.Client.DI;
using KSeF.Client.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KsefMinimal
{
    public class Program
    {
        private static ServiceProvider _serviceProvider = default!;
        private static IServiceScope _scope = default!;
        private static IKSeFClient KsefClient => _scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        private static IAuthorizationClient AuthorizationClient => _scope.ServiceProvider.GetRequiredService<IAuthorizationClient>();
        private static ICryptographyService CryptographyService => _scope.ServiceProvider.GetRequiredService<ICryptographyService>();
        private static string? _accessToken = null;
        
        private static async Task Main(string[] args)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false);

            IConfiguration config = builder.Build();

            var ksefSettings = config.GetSection("KsefSettings").Get<KsefSettings>();
            Console.WriteLine($"NIP: {ksefSettings.Nip}");
            
            var services = new ServiceCollection();
            ConfigureServices(services, config, ksefSettings.BaseUrl);
            _accessToken = await GetAccessTokenAsync(ksefSettings.Nip, ksefSettings.Token);
            Console.WriteLine($"accessToken: {_accessToken}");

            //const string ksefReferenceNumber = "5242764991-20251204-010040171D43-1C";
            //var invoiceSummary = await GetInvoiceSummary(ksefReferenceNumber);
            //Console.WriteLine($"invoiceSummary InvoiceNumber: {invoiceSummary.InvoiceNumber}");

            var sendInvoiceResponse = await SendInvoiceBasedOnTemplate();
            Console.WriteLine($"sendInvoiceResponse ReferenceNumber: {sendInvoiceResponse.ReferenceNumber}");
        }

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration, string baseUrl)
        {
            CryptographyConfigInitializer.EnsureInitialized();
            services.AddKSeFClient(options =>
            {
                options.BaseUrl = baseUrl;
            });
            
            services.AddCryptographyClient();
            
            services.AddSingleton<ICryptographyClient, CryptographyClient>();
            services.AddSingleton<ICertificateFetcher, DefaultCertificateFetcher>();
            services.AddSingleton<ICryptographyService, CryptographyService>();
            // // Rejestracja usługi hostowanej (Hosted Service) jako singleton na potrzeby testów
            services.AddSingleton<CryptographyWarmupHostedService>();
            //
            _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
             {
                 ValidateOnBuild = true,
                 ValidateScopes = true
            });
            
            _scope = _serviceProvider.CreateScope();
            //
            _scope.ServiceProvider.GetRequiredService<CryptographyWarmupHostedService>()
                 .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        private static async Task<string> GetAccessTokenAsync(string nip, string ksefToken)
        {
            const AuthenticationTokenContextIdentifierType contextType = AuthenticationTokenContextIdentifierType.Nip;
            AuthenticationTokenAuthorizationPolicy? authorizationPolicy = null;
            IAuthCoordinator authCoordinator = new AuthCoordinator(AuthorizationClient);
        
            // Act
            var result = await authCoordinator.AuthKsefTokenAsync(
                contextType,
                nip,
                ksefToken,
                CryptographyService,
                EncryptionMethodEnum.Rsa,
                authorizationPolicy,
                CancellationToken.None
            );
            
            return result.AccessToken.Token;
        }

        private static async Task<InvoiceSummary> GetInvoiceSummary(string ksefReferenceNumber)
        {
            InvoiceQueryFilters invoiceMetadataQueryRequest = new()
            {
                KsefNumber = ksefReferenceNumber,
                DateRange = new DateRange
                {
                    From = DateTime.Parse("2025-12-04T09:46:26.5411214Z", CultureInfo.InvariantCulture),
                    To = DateTime.Parse("2025-12-04T09:46:26.5411214Z", CultureInfo.InvariantCulture).AddMinutes(1),
                    DateType = DateType.Issue
                }
            };
            
            var metadata = await KsefClient.QueryInvoiceMetadataAsync(
                requestPayload: invoiceMetadataQueryRequest,
                accessToken: _accessToken);
        
            var invoiceMetadata = metadata.Invoices.Single(x => x.KsefNumber == ksefReferenceNumber);
            return invoiceMetadata;
        }

        private static async Task<SendInvoiceResponse> SendInvoiceBasedOnTemplate()
        {
            var templateInvoicePath = Path.Combine(AppContext.BaseDirectory, "Templates", "TestFaktura.xml");
            if (!File.Exists(templateInvoicePath))
            {
                throw new DirectoryNotFoundException($"Template invoice nie znaleziono pod: {templateInvoicePath}");
            }
            
            string templateInvoiceXml = await File.ReadAllTextAsync(templateInvoicePath);
            XDocument doc = XDocument.Parse(templateInvoiceXml, LoadOptions.PreserveWhitespace);
            doc = SetDocXmlElement(doc, "DataWytworzeniaFa", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            doc = SetDocXmlElement(doc, "P_1", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            doc = SetDocXmlElement(doc, "P_6", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            doc = SetDocXmlElement(doc, "P_2", "FV NI-3/12/2025");
            doc = SetDocXmlElement(doc, "P_7", "Złote konto - pakiet miesięczny");
            doc = SetDocXmlElement(doc, "DataZaplaty", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            doc = SetPodmiot2(doc);
            
            return await SendInvoice(doc.ToString(SaveOptions.DisableFormatting));
        }
        
        private static async Task<SendInvoiceResponse> SendInvoice(string invoiceXml)
        {
            var encryptionData = CryptographyService.GetEncryptionData();
            var openSessionRequest = await OnlineSessionUtils.OpenOnlineSessionAsync(KsefClient,
                encryptionData,
                _accessToken,
                SystemCode.FA3);

            var sendInvoiceResponse = await OnlineSessionUtils.SendInvoice(KsefClient,
                openSessionRequest.ReferenceNumber, _accessToken, encryptionData, CryptographyService, invoiceXml);
            return sendInvoiceResponse;
        }
        
        private static XDocument SetDocXmlElement(XDocument doc, string localName, string value)
        {
            XElement el = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)
                          ?? throw new InvalidOperationException($"Element '{localName}' nie znaleziono w doc.");
            el.Value = value;
            return doc;
        }
    
        private static XDocument SetPodmiot2(XDocument doc)
        {
            XElement? podmiot2 = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Podmiot2");
        
            XElement? nazwa = podmiot2?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Nazwa");
            nazwa.Value = "Nowa Nazwa Kupca";
        
            return doc;
        }
        
    }
}