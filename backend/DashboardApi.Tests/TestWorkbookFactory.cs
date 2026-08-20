using System.Net.Http.Headers;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;

namespace DashboardApi.Tests;

internal static class TestWorkbookFactory
{
    public static byte[] Create(params string[] sheetNames)
    {
        using var workbook = new XLWorkbook();
        foreach (var sheetName in sheetNames)
        {
            var sheet = workbook.AddWorksheet(sheetName);
            sheet.Cell("A1").Value = "Tanque";
            sheet.Cell("B1").Value = "Residual THPS";
            sheet.Cell("C1").Value = "Nota";
            sheet.Cell("D1").Value = "Fecha";
            sheet.Cell("E1").Value = "Precisión";
            sheet.Cell("A2").Value = 0;
            sheet.Cell("C2").Value = "<10 ppm";
            sheet.Cell("D2").Value = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Unspecified);
            sheet.Cell("E2").Value = "0.07945967421533573";
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public static async Task<DefaultHttpContext> CreateMultipartRequestAsync(
        byte[] workbook,
        string fieldName = "file",
        string fileName = "dataset.xlsx")
    {
        using var multipart = new MultipartFormDataContent("thps-test-boundary");
        using var fileContent = new ByteArrayContent(workbook);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        multipart.Add(fileContent, fieldName, fileName);

        var body = await multipart.ReadAsByteArrayAsync();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = multipart.Headers.ContentType!.ToString();
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        return context;
    }
}
