using KSeF.Client.Api.Builders.Online;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models.Invoices;
using KSeF.Client.Core.Models.Sessions;
using KSeF.Client.Core.Models.Sessions.OnlineSession;
using System.Text;

namespace KsefMinimal;

/// <summary>
/// Zawiera metody pomocnicze do obsługi sesji online w systemie KSeF.
/// https://github.com/CIRFMF/ksef-client-csharp/blob/main/KSeF.Client.Tests.Utils/OnlineSessionUtils.cs
/// </summary>
public static class OnlineSessionUtils
{
    private const SystemCode DefaultSystemCode = SystemCode.FA3;
    
    /// <summary>
    /// Otwiera nową sesję online w systemie KSeF.
    /// </summary>
    /// <param name="ksefClient">Klient KSeF.</param>
    /// <param name="encryptionData">Dane szyfrowania.</param>
    /// <param name="accessToken">Token dostępu.</param>
    /// <param name="systemCode">Kod systemowy formularza.</param>
    /// <param name="schemaVersion">Wersja schematu.</param>
    /// <param name="value">Wartość formularza.</param>
    /// <returns>Odpowiedź z informacjami o otwartej sesji online.</returns>
    public static async Task<OpenOnlineSessionResponse> OpenOnlineSessionAsync(IKSeFClient ksefClient,
        EncryptionData encryptionData,
        string accessToken,
        SystemCode systemCode = DefaultSystemCode)
    {
        OpenOnlineSessionRequest openOnlineSessionRequest = OpenOnlineSessionRequestBuilder
          .Create()
          .WithFormCode(systemCode: SystemCodeHelper.GetSystemCode(systemCode), schemaVersion: SystemCodeHelper.GetSchemaVersion(systemCode), value: SystemCodeHelper.GetValue(systemCode))
          .WithEncryption(
              encryptedSymmetricKey: encryptionData.EncryptionInfo.EncryptedSymmetricKey,
              initializationVector: encryptionData.EncryptionInfo.InitializationVector)
          .Build();

        return await ksefClient.OpenOnlineSessionAsync(openOnlineSessionRequest, accessToken).ConfigureAwait(false);
    }

    public static async Task<SendInvoiceResponse> SendInvoice(IKSeFClient ksefClient, string sessionReferenceNumber, string accessToken, EncryptionData encryptionData, ICryptographyService cryptographyService, string xml)
    {
        using MemoryStream memoryStream = new(Encoding.UTF8.GetBytes(xml));
        byte[] invoice = memoryStream.ToArray();

        byte[] encryptedInvoice = cryptographyService.EncryptBytesWithAES256(invoice, encryptionData.CipherKey, encryptionData.CipherIv);
        FileMetadata invoiceMetadata = cryptographyService.GetMetaData(invoice);
        FileMetadata encryptedInvoiceMetadata = cryptographyService.GetMetaData(encryptedInvoice);

        SendInvoiceRequest sendOnlineInvoiceRequest = SendInvoiceOnlineSessionRequestBuilder
            .Create()
            .WithInvoiceHash(invoiceMetadata.HashSHA, invoiceMetadata.FileSize)
            .WithEncryptedDocumentHash(
               encryptedInvoiceMetadata.HashSHA, encryptedInvoiceMetadata.FileSize)
            .WithEncryptedDocumentContent(Convert.ToBase64String(encryptedInvoice))
            .Build();

        return await ksefClient.SendOnlineSessionInvoiceAsync(sendOnlineInvoiceRequest, sessionReferenceNumber, accessToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Zamyka sesję online w systemie KSeF.
    /// </summary>
    /// <param name="kSeFClient">Klient KSeF.</param>
    /// <param name="sessionReferenceNumber">Numer referencyjny sesji.</param>
    /// <param name="accessToken">Token dostępu.</param>
    public static async Task CloseOnlineSessionAsync(IKSeFClient kSeFClient, string sessionReferenceNumber, string accessToken)
    {
        await kSeFClient.CloseOnlineSessionAsync(sessionReferenceNumber, accessToken).ConfigureAwait(false);
    }
}