using System.Text;
using Microsoft.AspNetCore.Mvc.Formatters;

public class TextPlainInputFormatter : InputFormatter
{
    public TextPlainInputFormatter()
    {
        SupportedMediaTypes.Add("text/plain");
    }

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context)
    {
        var request = context.HttpContext.Request;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8))
        {
            try
            {
                var content = await reader.ReadToEndAsync();
                return await InputFormatterResult.SuccessAsync(content);
            }
            catch
            {
                return await InputFormatterResult.FailureAsync();
            }
        }
    }

    protected override bool CanReadType(Type type)
    {
        return type == typeof(string);
    }
}