using System.Text;
using MaliMusavir.Api.Domain;
using MaliMusavir.Api.Infrastructure;
using MaliMusavir.Api.Ocr;
using Xunit;

namespace MaliMusavir.Tests;

public class BankStatementParserTests
{
    [Fact]
    public void ParsesMt940CreditDebitAndCounterpartyIdentifiers()
    {
        const string mt940 = ":20:REF-1\n:25:TR12 0006 1005 1978 6457 8413 26\n:60F:C260801TRY1000,00\n:61:2608020802C1250,50NTRFNONREF//BANK-1\n:86:?20GONDEREN: ACME LTD VKN:1234567890 ?21TR12 0006 1005 1978 6457 8413 26\n:61:260803D75,25NTRFREF-2\n:86:ALICI: BETA TICARET A.S.\n:62F:C260803TRY2175,25";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(mt940));
        var result = BankStatementParser.Parse(stream, "ekstre.mt940");

        Assert.Equal("MT940", result.Format);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(BankDirection.Gelen, result.Lines[0].Direction);
        Assert.Equal(1250.50m, result.Lines[0].Amount);
        Assert.Equal("1234567890", result.Lines[0].CounterpartyTaxId);
        Assert.Equal(BankDirection.Giden, result.Lines[1].Direction);
        Assert.Equal(75.25m, result.Lines[1].Amount);
        Assert.Equal(2175.25m, result.ClosingBalance);
    }

    [Fact]
    public void ParsesCsvTurkishNumbersQuotedDescriptionsAndSignedAmounts()
    {
        const string csv = "Tarih;Tutar;Açıklama;Karşı Taraf;IBAN;Referans No\n01.08.2026;\"1.234,56\";\"EFT, kira ödemesi\";\"ALFA LTD\";TR120006100519786457841326;R-1\n02/08/2026;-25,00;Komisyon;Banka;;R-2";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var result = BankStatementParser.Parse(stream, "ekstre.csv");

        Assert.Equal("CSV", result.Format);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(1234.56m, result.Lines[0].Amount);
        Assert.Equal(BankDirection.Gelen, result.Lines[0].Direction);
        Assert.Equal("TR120006100519786457841326", result.Lines[0].CounterpartyIban);
        Assert.Equal(BankDirection.Giden, result.Lines[1].Direction);
        Assert.Equal(25m, result.Lines[1].Amount);
    }

    [Theory]
    [InlineData("1.234,56 TL", 1234.56)]
    [InlineData("(125,40)", -125.40)]
    [InlineData("1,234.50", 1234.50)]
    public void ParsesTurkishAmounts(string text, decimal expected)
    {
        Assert.Equal(expected, BankStatementParser.ParseTurkishAmount(text));
    }

    [Fact]
    public void RejectsTabularStatementWithoutRecognizedHeader()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Banka Ekstresi\nBu dosya hareket basligi icermiyor"));
        var result = BankStatementParser.Parse(stream, "bozuk.csv");

        Assert.Empty(result.Lines);
        Assert.Contains("başlık", result.Warning!, StringComparison.OrdinalIgnoreCase);
    }
}

public class OcrValidationTests
{
    [Fact]
    public void ParsesJsonFenceAndStringNumbers()
    {
        const string response = "```json\n{\"vendorName\":\"Ornek Market\",\"totalAmount\":\"120.00\",\"vatRate\":\"20\",\"vatAmount\":\"20\",\"netAmount\":\"100\"}\n```";

        var raw = OcrJsonParser.Deserialize(response);

        Assert.NotNull(raw);
        Assert.Equal(120m, raw!.TotalAmount);
        Assert.Equal(20m, raw.VatRate);
    }

    [Fact]
    public void NormalizesMultiRateReceiptUsingBreakdownTotals()
    {
        var raw = new LlmReceiptExtraction
        {
            VendorName = "Ornek Market",
            ReceiptDate = "2026-08-01",
            TotalAmount = 341m,
            VatBreakdown = new List<LlmVatBreakdownRow>
            {
                new() { Rate = 1m, Net = 100m, Vat = 1m },
                new() { Rate = 20m, Net = 200m, Vat = 40m }
            },
            Confidence = new Dictionary<string, double> { ["totalAmount"] = 0.95, ["vatBreakdown"] = 0.9 }
        };

        var result = new OcrValidator().Normalize(raw);

        Assert.Equal(300m, result.NetAmount);
        Assert.Equal(41m, result.VatAmount);
        Assert.Equal(341m, result.TotalAmount);
        Assert.Equal(2, result.VatBreakdown.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void WarnsForInconsistentReceiptVat()
    {
        var raw = new LlmReceiptExtraction
        {
            ReceiptDate = "2026-08-01",
            TotalAmount = 200m,
            NetAmount = 100m,
            VatRate = 20m,
            VatAmount = 10m,
            Confidence = new Dictionary<string, double> { ["totalAmount"] = 0.9 }
        };

        var result = new OcrValidator().Normalize(raw);

        Assert.NotEmpty(result.Warnings);
        Assert.True(result.Confidence < 0.9);
    }

    [Fact]
    public void NormalizesZReportAndWarnsWhenPaymentsDoNotMatchSales()
    {
        var raw = new LlmReceiptExtraction
        {
            OkcSerialNumber = "OKC-1",
            ZNumber = "1234",
            ReportDate = "2026-08-01",
            TotalSales = 120m,
            TotalVat = 20m,
            CashTotal = 50m,
            CardTotal = 60m,
            VatBreakdown = new List<LlmVatBreakdownRow> { new() { Rate = 20m, Net = 100m, Vat = 20m } },
            Confidence = new Dictionary<string, double> { ["totalSales"] = 0.95 }
        };

        var result = new OcrValidator().Normalize(raw, OcrDocumentKind.ZRaporu);

        Assert.Equal(120m, result.TotalAmount);
        Assert.Equal("1234", result.DocumentNumber);
        Assert.NotEmpty(result.Warnings);
    }
}
