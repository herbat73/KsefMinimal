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
using KsefMinimal.Models;
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
        private static  IVerificationLinkService VerificationLinkService => _scope.ServiceProvider.GetRequiredService<IVerificationLinkService>();
        private static string? _accessToken = null;
        private static string? _openSessionReferenceNumber =  null;
        
        private static async Task Main(string[] args)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false);

            IConfiguration config = builder.Build();

            var ksefSettings = config.GetSection(nameof(KsefSettings)).Get<KsefSettings>();
            Console.WriteLine($"BaseUrl: {ksefSettings.BaseUrl}");
            
            var sellerSettings = config.GetSection("SellerSettings").Get<CompanyInfo>();
            Console.WriteLine($"VatId: {sellerSettings.VatId}");
            
            var services = new ServiceCollection();
            ConfigureServices(services, config, ksefSettings.BaseUrl);
            _accessToken = await GetAccessTokenAsync(sellerSettings.VatId, ksefSettings.Token);
            Console.WriteLine($"accessToken: {_accessToken}");

            var invoiceDate = DateTime.Now;
            var productLineInfo = GetTestProductLineInfo();
            var sendInvoiceResponse = await SendInvoiceBasedOnTemplate(invoiceDate, sellerSettings, productLineInfo);
            Console.WriteLine($"sendInvoiceResponse ReferenceNumber: {sendInvoiceResponse.ReferenceNumber}");

            var sendInvoiceStatus = await OnlineSessionUtils.GetSessionInvoiceStatusAsync(KsefClient,
                _openSessionReferenceNumber,
                sendInvoiceResponse.ReferenceNumber,
                _accessToken);

            var ksefReferenceNumber = sendInvoiceStatus.KsefNumber;
            Console.WriteLine($"ksefReferenceNumber: {ksefReferenceNumber} sendInvoiceStatus Code: {sendInvoiceStatus.Status.Code}");

            await Task.Delay(TimeSpan.FromSeconds(10));
            
            var invoiceSummary = await GetInvoiceSummary(ksefReferenceNumber, invoiceDate);
            Console.WriteLine($"invoiceSummary InvoiceNumber: {invoiceSummary.InvoiceNumber}");
            
            var invoiceHash = invoiceSummary.InvoiceHash;
            var invoicingDate = invoiceSummary.InvoicingDate;
            
            Console.WriteLine($"invoiceSummary invoiceHash: {invoiceHash}");
            Console.WriteLine($"invoiceSummary invoicingDate: {invoicingDate}");
            
            var invoiceForOnlineUrl = VerificationLinkService.BuildInvoiceVerificationUrl( sellerSettings.VatId, invoicingDate.DateTime, invoiceHash);
            
            Console.WriteLine($"invoiceForOnlineUrl: {invoiceForOnlineUrl}");
            
            await CloseSession();
            Console.WriteLine($"Session {_openSessionReferenceNumber} closed");
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
            // Rejestracja usługi hostowanej (Hosted Service) jako singleton na potrzeby testów
            services.AddSingleton<CryptographyWarmupHostedService>();
            
            _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
             {
                 ValidateOnBuild = true,
                 ValidateScopes = true
            });
            
            _scope = _serviceProvider.CreateScope();
            
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

        private static async Task<InvoiceSummary> GetInvoiceSummary(string ksefReferenceNumber, DateTime invoiceDate)
        {
            InvoiceQueryFilters invoiceMetadataQueryRequest = new()
            {
                KsefNumber = ksefReferenceNumber,
                DateRange = new DateRange
                {
                    From = invoiceDate.AddMinutes(-10),
                    To = invoiceDate.AddMinutes(10),
                    DateType = DateType.Issue
                }
            };
            
            var metadata = await KsefClient.QueryInvoiceMetadataAsync(
                requestPayload: invoiceMetadataQueryRequest,
                accessToken: _accessToken);
        
            var invoiceMetadata = metadata.Invoices.Single(x => x.KsefNumber == ksefReferenceNumber);
            return invoiceMetadata;
        }

        private static async Task<SendInvoiceResponse> SendInvoiceBasedOnTemplate(DateTime invoiceDate, CompanyInfo sellerSettings, ProductLineInfo productLineInfo)
        {
            var start = new DateTime(invoiceDate.Year, invoiceDate.Month, invoiceDate.Day);
            var elapsedTicks = invoiceDate.Ticks - start.Ticks; 
            var invoiceNumber = $"FV {invoiceDate.Year}/{invoiceDate.Month}/{invoiceDate.Day}/{elapsedTicks}";
            Console.WriteLine($"invoiceNumber {invoiceNumber}");
            
            var templateInvoicePath = Path.Combine(AppContext.BaseDirectory, "Templates", "TestFaktura.xml");
            if (!File.Exists(templateInvoicePath))
            {
                throw new DirectoryNotFoundException($"Template invoice nie znaleziono pod: {templateInvoicePath}");
            } 
            
            var templateInvoiceXml = await File.ReadAllTextAsync(templateInvoicePath);
            var doc = XDocument.Parse(templateInvoiceXml, LoadOptions.PreserveWhitespace);
            doc = SetDocXmlElement(doc, "DataWytworzeniaFa", invoiceDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            doc = SetDocXmlElement(doc, "P_1", invoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            doc = SetDocXmlElement(doc, "P_6", invoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            doc = SetDocXmlElement(doc, "P_2", invoiceNumber);
            doc = SetDocXmlElement(doc, "P_7", productLineInfo.ProductName);
            doc = SetDocXmlElement(doc, "P_8A", productLineInfo.Meassure);
            doc = SetDocXmlElement(doc, "P_8B", productLineInfo.Qty.ToString(CultureInfo.InvariantCulture));
            doc = SetDocXmlElement(doc, "P_9A", productLineInfo.NetPrice.ToString(CultureInfo.InvariantCulture));
            doc = SetDocXmlElement(doc, "P_11", (productLineInfo.NetPrice * productLineInfo.Qty).ToString(CultureInfo.InvariantCulture));
            doc = SetDocXmlElement(doc, "P_12", productLineInfo.VatRate.ToString(CultureInfo.InvariantCulture));
            doc = SetDocXmlElement(doc, "DataZaplaty", invoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            // sprzedawca
            doc = SetPodmiotInfo(doc, sellerSettings, "Podmiot1");
            // odbiorca
            var buyerInfo = GetBuyerTestInfo();
            doc = SetPodmiotInfo(doc, buyerInfo, "Podmiot2");
            
            return await SendInvoice(doc.ToString(SaveOptions.DisableFormatting));
        }
        
        private static async Task<SendInvoiceResponse> SendInvoice(string invoiceXml)
        {
            var encryptionData = CryptographyService.GetEncryptionData();
            var openSessionRequest = await OnlineSessionUtils.OpenOnlineSessionAsync(KsefClient,
                encryptionData,
                _accessToken,
                SystemCode.FA3);

            _openSessionReferenceNumber = openSessionRequest.ReferenceNumber;

            var sendInvoiceResponse = await OnlineSessionUtils.SendInvoice(KsefClient,
                _openSessionReferenceNumber, _accessToken, encryptionData, CryptographyService, invoiceXml);
            
            return sendInvoiceResponse;
        }
        
        private static XDocument SetDocXmlElement(XDocument doc, string localName, string value)
        {
            XElement el = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)
                          ?? throw new InvalidOperationException($"Element '{localName}' nie znaleziono w doc.");
            el.Value = value;
            return doc;
        }
    
        private static XDocument SetPodmiotInfo(XDocument doc, CompanyInfo companyInfo, string podmiotId)
        {
            var podmiot2 = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == podmiotId);
            var nazwa = podmiot2?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Nazwa");
            nazwa.Value = companyInfo.Name;
            var nip = podmiot2?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NIP");
            nip.Value = companyInfo.VatId;
            var kodKraju = podmiot2?.Descendants().FirstOrDefault(e => e.Name.LocalName == "KodKraju");
            kodKraju.Value = companyInfo.CountryCode;
            var adresL1 = podmiot2?.Descendants().FirstOrDefault(e => e.Name.LocalName == "AdresL1");
            adresL1.Value = companyInfo.AdresLine1;
            var adresL2 = podmiot2?.Descendants().FirstOrDefault(e => e.Name.LocalName == "AdresL2");
            adresL2.Value = companyInfo.AdresLine2;
            return doc;
        }
        
        private static async Task CloseSession()
        {
            await OnlineSessionUtils.CloseOnlineSessionAsync(KsefClient, _openSessionReferenceNumber, _accessToken);
        }

        private static CompanyInfo GetBuyerTestInfo()
        {
            var buyerInfo = new CompanyInfo
            {
                VatId = "7740001454",
                Name = "ORLEN SPÓŁKA AKCYJNA",
                AdresLine1 = " ul. Chemików 7",
                AdresLine2 = "09-411 Płock",
                CountryCode = "PL"
            };
            return buyerInfo;
        }

        private static ProductLineInfo GetTestProductLineInfo()
        {
            var productLineInfo = new ProductLineInfo()
            {
                ProductName = "Złote konto w systemie SaaS - roczne",
                NetPrice = 100,
                VatRate = 23,
                Qty = 1,
                Meassure = "szt"
            };
            return productLineInfo;
        }
    }
}