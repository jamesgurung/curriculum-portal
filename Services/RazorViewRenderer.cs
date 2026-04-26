using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using System.Globalization;

namespace CurriculumPortal;

public interface IRazorViewRenderer
{
  Task<string> RenderAsync<TModel>(ActionContext actionContext, string viewPath, TModel model);
}

public class RazorViewRenderer(IRazorViewEngine razorViewEngine, ITempDataProvider tempDataProvider) : IRazorViewRenderer
{
  public async Task<string> RenderAsync<TModel>(ActionContext actionContext, string viewPath, TModel model)
  {
    ArgumentNullException.ThrowIfNull(actionContext);
    ArgumentException.ThrowIfNullOrWhiteSpace(viewPath);

    var viewResult = razorViewEngine.GetView(executingFilePath: null, viewPath, isMainPage: true);
    if (!viewResult.Success)
    {
      viewResult = razorViewEngine.FindView(actionContext, viewPath, isMainPage: true);
    }

    if (!viewResult.Success)
    {
      var searchedLocations = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
      throw new InvalidOperationException($"Unable to find view '{viewPath}'.{Environment.NewLine}{searchedLocations}");
    }

    await using var writer = new StringWriter(CultureInfo.InvariantCulture);
    var viewData = new ViewDataDictionary<TModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
    {
      Model = model
    };
    var tempData = new TempDataDictionary(actionContext.HttpContext, tempDataProvider);
    var viewContext = new ViewContext(actionContext, viewResult.View, viewData, tempData, writer, new HtmlHelperOptions());

    await viewResult.View.RenderAsync(viewContext);
    return writer.ToString();
  }
}
