import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { RazorExtractor } from '../../extractors/razor-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('RazorExtractor', () => {
  let parserManager: ParserManager;

    beforeAll(async () => {
    // Initialize logger for tests
    const { initializeLogger } = await import('../../utils/logger.js');
    const { MillerPaths } = await import('../../utils/miller-paths.js');
    const paths = new MillerPaths(process.cwd());
    await paths.ensureDirectories();
    initializeLogger(paths);

    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Razor Pages and Directives', () => {
    it('should extract page directives, model bindings, and basic Razor syntax', async () => {
      const razorCode = `@page "/products/{id:int?}"
@model ProductDetailsModel
@using Microsoft.AspNetCore.Authorization
@using MyApp.Models
@inject ILogger<ProductDetailsModel> Logger
@inject IProductService ProductService
@attribute [Authorize]

@{
    ViewData["Title"] = "Product Details";
    Layout = "_Layout";

    var isLoggedIn = User.Identity.IsAuthenticated;
    var productId = Model.ProductId;
    var displayName = Model.Product?.Name ?? "Unknown Product";
}

<div class="product-container">
    <h1>@displayName</h1>

    @if (Model.Product != null)
    {
        <div class="product-details">
            <p>Price: @Model.Product.Price.ToString("C")</p>
            <p>Description: @Html.Raw(Model.Product.Description)</p>

            @if (Model.Product.IsOnSale)
            {
                <span class="sale-badge">ON SALE!</span>
            }
            else
            {
                <span class="regular-price">Regular Price</span>
            }
        </div>
    }
    else
    {
        <div class="error-message">
            <p>Product not found.</p>
            <a href="/products" class="btn btn-primary">Back to Products</a>
        </div>
    }

    @foreach (var review in Model.Reviews ?? Enumerable.Empty<Review>())
    {
        <div class="review">
            <h4>@review.Title</h4>
            <p>Rating: @(new string('★', review.Rating))</p>
            <p>@review.Comment</p>
            <small>By @review.AuthorName on @review.CreatedAt.ToString("MMMM dd, yyyy")</small>
        </div>
    }

    @switch (Model.Product?.Category)
    {
        case "Electronics":
            <partial name="_ElectronicsInfo" model="Model.Product" />
            break;
        case "Clothing":
            <partial name="_ClothingInfo" model="Model.Product" />
            break;
        default:
            <p>Category: @Model.Product?.Category</p>
            break;
    }
</div>

@section Scripts {
    <script>
        window.productId = @productId;

        document.addEventListener('DOMContentLoaded', function() {
            console.log('Product page loaded for ID:', @productId);
        });
    </script>
}

@section Styles {
    <style>
        .product-container {
            max-width: 800px;
            margin: 0 auto;
        }

        .sale-badge {
            color: red;
            font-weight: bnew;
        }
    </style>
}`;

      const result = await parserManager.parseFile('ProductDetails.razor', razorCode);
      const extractor = new RazorExtractor('razor', 'ProductDetails.razor', razorCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Page directive
      const pageDirective = symbols.find(s => s.name === '@page');
      expect(pageDirective).toBeDefined();
      expect(pageDirective?.kind).toBe(SymbolKind.Import);
      expect(pageDirective?.signature).toContain('/products/{id:int?}');

      // Model directive
      const modelDirective = symbols.find(s => s.name === '@model');
      expect(modelDirective).toBeDefined();
      expect(modelDirective?.signature).toContain('ProductDetailsModel');

      // Using directives
      const usingAuth = symbols.find(s => s.name === 'Microsoft.AspNetCore.Authorization');
      expect(usingAuth).toBeDefined();
      expect(usingAuth?.kind).toBe(SymbolKind.Import);

      const usingModels = symbols.find(s => s.name === 'MyApp.Models');
      expect(usingModels).toBeDefined();

      // Inject directives
      const loggerInject = symbols.find(s => s.name === 'Logger');
      expect(loggerInject).toBeDefined();
      expect(loggerInject?.kind).toBe(SymbolKind.Property);
      expect(loggerInject?.signature).toContain('@inject ILogger<ProductDetailsModel> Logger');

      const serviceInject = symbols.find(s => s.name === 'ProductService');
      expect(serviceInject).toBeDefined();
      expect(serviceInject?.signature).toContain('@inject IProductService ProductService');

      // Attribute directive
      const attributeDirective = symbols.find(s => s.name === '@attribute');
      expect(attributeDirective).toBeDefined();
      expect(attributeDirective?.signature).toContain('[Authorize]');

      // Code block variables
      const isLoggedIn = symbols.find(s => s.name === 'isLoggedIn');
      expect(isLoggedIn).toBeDefined();
      expect(isLoggedIn?.kind).toBe(SymbolKind.Variable);
      expect(isLoggedIn?.signature).toContain('User.Identity.IsAuthenticated');

      const productId = symbols.find(s => s.name === 'productId');
      expect(productId).toBeDefined();
      expect(productId?.signature).toContain('Model.ProductId');

      const displayName = symbols.find(s => s.name === 'displayName');
      expect(displayName).toBeDefined();
      expect(displayName?.signature).toContain('Model.Product?.Name ?? "Unknown Product"');

      // ViewData assignment
      const viewDataTitle = symbols.find(s => s.signature?.includes('ViewData["Title"]'));
      expect(viewDataTitle).toBeDefined();

      // Section blocks
      const scriptsSection = symbols.find(s => s.name === 'Scripts');
      expect(scriptsSection).toBeDefined();
      expect(scriptsSection?.kind).toBe(SymbolKind.Module);
      expect(scriptsSection?.signature).toContain('@section Scripts');

      const stylesSection = symbols.find(s => s.name === 'Styles');
      expect(stylesSection).toBeDefined();
      expect(stylesSection?.signature).toContain('@section Styles');
    });
  });

  describe('Razor Components and Parameters', () => {
    it('should extract component parameters, events, and lifecycle methods', async () => {
      const razorCode = `@namespace MyApp.Components
@inherits ComponentBase
@implements IDisposable
@inject IJSRuntime JSRuntime
@inject NavigationManager Navigation

<div class="user-card @CssClass" @onclick="HandleClick" @onmouseover="HandleMouseOver">
    <img src="@AvatarUrl" alt="@DisplayName" class="avatar" />

    <div class="user-info">
        <h3>@DisplayName</h3>
        <p>@Email</p>

        @if (ShowStatus)
        {
            <span class="status @(IsOnline ? "online" : "offline")">
                @(IsOnline ? "Online" : "Offline")
            </span>
        }

        @if (ChildContent != null)
        {
            <div class="user-actions">
                @ChildContent
            </div>
        }
    </div>

    @if (IsEditing)
    {
        <EditForm Model="EditModel" OnValidSubmit="HandleSubmit">
            <DataAnnotationsValidator />
            <ValidationSummary />

            <div class="form-group">
                <label for="displayName">Display Name:</label>
                <InputText id="displayName" @bind-Value="EditModel.DisplayName" class="form-control" />
                <ValidationMessage For="@(() => EditModel.DisplayName)" />
            </div>

            <div class="form-group">
                <label for="email">Email:</label>
                <InputText id="email" @bind-Value="EditModel.Email" type="email" class="form-control" />
                <ValidationMessage For="@(() => EditModel.Email)" />
            </div>

            <div class="form-actions">
                <button type="submit" class="btn btn-primary" disabled="@IsSubmitting">
                    @if (IsSubmitting)
                    {
                        <span class="spinner-border spinner-border-sm" role="status"></span>
                        <span>Saving...</span>
                    }
                    else
                    {
                        <span>Save Changes</span>
                    }
                </button>
                <button type="button" class="btn btn-secondary" @onclick="CancelEdit">Cancel</button>
            </div>
        </EditForm>
    }
</div>

@code {
    [Parameter] public string? DisplayName { get; set; }
    [Parameter] public string? Email { get; set; }
    [Parameter] public string? AvatarUrl { get; set; }
    [Parameter] public bool IsOnline { get; set; }
    [Parameter] public bool ShowStatus { get; set; } = true;
    [Parameter] public string CssClass { get; set; } = "";
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public EventCallback<MouseEventArgs> OnClick { get; set; }
    [Parameter] public EventCallback<UserUpdatedEventArgs> OnUserUpdated { get; set; }

    [CascadingParameter] public ThemeProvider? Theme { get; set; }
    [CascadingParameter(Name = "UserContext")] public UserContext? UserContext { get; set; }

    private bool IsEditing { get; set; }
    private bool IsSubmitting { get; set; }
    private UserEditModel EditModel { get; set; } = new();
    private IJSObjectReference? jsModule;

    protected override async Task OnInitializedAsync()
    {
        if (string.IsNullOrEmpty(AvatarUrl))
        {
            AvatarUrl = "/images/default-avatar.png";
        }

        EditModel.DisplayName = DisplayName;
        EditModel.Email = Email;

        jsModule = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/UserCard.razor.js");
    }

    protected override async Task OnParametersSetAsync()
    {
        if (EditModel.DisplayName != DisplayName || EditModel.Email != Email)
        {
            EditModel.DisplayName = DisplayName;
            EditModel.Email = Email;
            StateHasChanged();
        }
    }

    protected override bool ShouldRender()
    {
        return !IsSubmitting;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && jsModule != null)
        {
            await jsModule.InvokeVoidAsync("initialize", DotNetObjectReference.Create(this));
        }
    }

    private async Task HandleClick(MouseEventArgs args)
    {
        await OnClick.InvokeAsync(args);
    }

    private void HandleMouseOver(MouseEventArgs args)
    {
        // Handle mouse over
    }

    private async Task HandleSubmit()
    {
        IsSubmitting = true;
        StateHasChanged();

        try
        {
            // Simulate API call
            await Task.Delay(1000);

            DisplayName = EditModel.DisplayName;
            Email = EditModel.Email;
            IsEditing = false;

            await OnUserUpdated.InvokeAsync(new UserUpdatedEventArgs
            {
                DisplayName = DisplayName,
                Email = Email
            });
        }
        finally
        {
            IsSubmitting = false;
            StateHasChanged();
        }
    }

    private void CancelEdit()
    {
        IsEditing = false;
        EditModel.DisplayName = DisplayName;
        EditModel.Email = Email;
    }

    [JSInvokable]
    public void OnJSCallback(string message)
    {
        // Handle JavaScript callback
        Console.WriteLine($"JS Callback: {message}");
    }

    public async ValueTask DisposeAsync()
    {
        if (jsModule != null)
        {
            await jsModule.DisposeAsync();
        }
    }

    void IDisposable.Dispose()
    {
        // Cleanup resources
    }
}

@functions {
    private string GetStatusCssClass()
    {
        return IsOnline ? "status-online" : "status-offline";
    }

    private static string FormatLastSeen(DateTime? lastSeen)
    {
        if (!lastSeen.HasValue) return "Never";

        var timeSpan = DateTime.UtcNow - lastSeen.Value;
        return timeSpan.Days > 0 ? $"{timeSpan.Days} days ago" :
               timeSpan.Hours > 0 ? $"{timeSpan.Hours} hours ago" : "Recently";
    }
}

<style>
    .user-card {
        display: flex;
        align-items: center;
        padding: 1rem;
        border: 1px solid #ddd;
        border-radius: 8px;
        cursor: pointer;
        transition: box-shadow 0.2s;
    }

    .user-card:hover {
        box-shadow: 0 2px 8px rgba(0,0,0,0.1);
    }

    .avatar {
        width: 48px;
        height: 48px;
        border-radius: 50%;
        margin-right: 1rem;
    }

    .status.online {
        color: green;
    }

    .status.offline {
        color: #999;
    }
</style>`;

      const result = await parserManager.parseFile('UserCard.razor', razorCode);
      const extractor = new RazorExtractor('razor', 'UserCard.razor', razorCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Namespace directive
      const namespaceDirective = symbols.find(s => s.name === '@namespace');
      expect(namespaceDirective).toBeDefined();
      expect(namespaceDirective?.signature).toContain('MyApp.Components');

      // Inherits directive
      const inheritsDirective = symbols.find(s => s.name === '@inherits');
      expect(inheritsDirective).toBeDefined();
      expect(inheritsDirective?.signature).toContain('ComponentBase');

      // Implements directive
      const implementsDirective = symbols.find(s => s.name === '@implements');
      expect(implementsDirective).toBeDefined();
      expect(implementsDirective?.signature).toContain('IDisposable');

      // Parameters
      const displayNameParam = symbols.find(s => s.name === 'DisplayName' && s.signature?.includes('[Parameter]'));
      expect(displayNameParam).toBeDefined();
      expect(displayNameParam?.kind).toBe(SymbolKind.Property);
      expect(displayNameParam?.signature).toContain('[Parameter] public string? DisplayName');

      const emailParam = symbols.find(s => s.name === 'Email' && s.signature?.includes('[Parameter]'));
      expect(emailParam).toBeDefined();

      const childContentParam = symbols.find(s => s.name === 'ChildContent' && s.kind === SymbolKind.Property);
      expect(childContentParam).toBeDefined();
      expect(childContentParam?.signature).toContain('RenderFragment? ChildContent');

      // Event callback parameters
      const onClickParam = symbols.find(s => s.name === 'OnClick');
      expect(onClickParam).toBeDefined();
      expect(onClickParam?.signature).toContain('EventCallback<MouseEventArgs> OnClick');

      // Cascading parameters
      const themeParam = symbols.find(s => s.name === 'Theme');
      expect(themeParam).toBeDefined();
      expect(themeParam?.signature).toContain('[CascadingParameter] public ThemeProvider? Theme');

      const userContextParam = symbols.find(s => s.name === 'UserContext');
      expect(userContextParam).toBeDefined();
      expect(userContextParam?.signature).toContain('[CascadingParameter(Name = "UserContext")]');

      // Private fields
      const isEditing = symbols.find(s => s.name === 'IsEditing');
      expect(isEditing).toBeDefined();
      expect(isEditing?.signature).toContain('private bool IsEditing');

      const editModel = symbols.find(s => s.name === 'EditModel');
      expect(editModel).toBeDefined();
      expect(editModel?.signature).toContain('private UserEditModel EditModel');

      // Lifecycle methods
      const onInitialized = symbols.find(s => s.name === 'OnInitializedAsync');
      expect(onInitialized).toBeDefined();
      expect(onInitialized?.kind).toBe(SymbolKind.Method);
      expect(onInitialized?.signature).toContain('protected override async Task OnInitializedAsync()');

      const onParametersSet = symbols.find(s => s.name === 'OnParametersSetAsync');
      expect(onParametersSet).toBeDefined();
      expect(onParametersSet?.signature).toContain('protected override async Task OnParametersSetAsync()');

      const shouldRender = symbols.find(s => s.name === 'ShouldRender');
      expect(shouldRender).toBeDefined();
      expect(shouldRender?.signature).toContain('protected override bool ShouldRender()');

      const onAfterRender = symbols.find(s => s.name === 'OnAfterRenderAsync');
      expect(onAfterRender).toBeDefined();
      expect(onAfterRender?.signature).toContain('protected override async Task OnAfterRenderAsync(bool firstRender)');

      // Event handlers
      const handleClick = symbols.find(s => s.name === 'HandleClick');
      expect(handleClick).toBeDefined();
      expect(handleClick?.signature).toContain('private async Task HandleClick(MouseEventArgs args)');

      const handleSubmit = symbols.find(s => s.name === 'HandleSubmit');
      expect(handleSubmit).toBeDefined();

      // JSInvokable method
      const jsCallback = symbols.find(s => s.name === 'OnJSCallback');
      expect(jsCallback).toBeDefined();
      expect(jsCallback?.signature).toContain('[JSInvokable]');

      // Disposal methods
      const disposeAsync = symbols.find(s => s.name === 'DisposeAsync');
      expect(disposeAsync).toBeDefined();
      expect(disposeAsync?.signature).toContain('public async ValueTask DisposeAsync()');

      const dispose = symbols.find(s => s.name === 'Dispose');
      expect(dispose).toBeDefined();
      expect(dispose?.signature).toContain('void IDisposable.Dispose()');

      // Functions block
      const getStatusCssClass = symbols.find(s => s.name === 'GetStatusCssClass');
      expect(getStatusCssClass).toBeDefined();
      expect(getStatusCssClass?.signature).toContain('private string GetStatusCssClass()');

      const formatLastSeen = symbols.find(s => s.name === 'FormatLastSeen');
      expect(formatLastSeen).toBeDefined();
      expect(formatLastSeen?.signature).toContain('private static string FormatLastSeen(DateTime? lastSeen)');
    });
  });

  describe('Razor Layouts and Sections', () => {
    it('should extract layout inheritance, sections, and ViewImports', async () => {
      const razorCode = `@{
    Layout = "_Layout";
    ViewData["Title"] = "Home Page";
    ViewBag.MetaDescription = "Welcome to our amazing website";
}

@model HomePageModel

<div class="hero-section">
    <h1>@ViewData["Title"]</h1>
    <p class="lead">@Model.WelcomeMessage</p>

    @await Component.InvokeAsync("FeaturedProducts", new { count = 6 })
</div>

<div class="content-sections">
    @foreach (var section in Model.ContentSections)
    {
        <section class="content-section">
            <h2>@section.Title</h2>
            <div class="section-content">
                @Html.Raw(section.Content)
            </div>

            @if (section.HasCallToAction)
            {
                <div class="cta-section">
                    <a href="@section.CallToActionUrl" class="btn btn-primary">
                        @section.CallToActionText
                    </a>
                </div>
            }
        </section>
    }
</div>

@section MetaTags {
    <meta name="description" content="@ViewBag.MetaDescription" />
    <meta property="og:title" content="@ViewData["Title"]" />
    <meta property="og:description" content="@ViewBag.MetaDescription" />
    <meta property="og:image" content="@Url.Action("GetOgImage", "Home")" />
}

@section Scripts {
    <script src="~/js/home.js" asp-append-version="true"></script>
    <script>
        window.homeData = {
            welcomeMessage: '@Html.Raw(Json.Serialize(Model.WelcomeMessage))',
            sectionCount: @Model.ContentSections.Count,
            isAuthenticated: @Json.Serialize(User.Identity.IsAuthenticated)
        };
    </script>

    @{await Html.RenderPartialAsync("_AnalyticsScripts");}
}

@section Styles {
    <link rel="stylesheet" href="~/css/home.css" asp-append-version="true" />
    <style>
        .hero-section {
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            padding: 4rem 0;
            text-align: center;
        }

        .content-section {
            margin: 2rem 0;
            padding: 2rem;
            border-radius: 8px;
            box-shadow: 0 2px 4px rgba(0,0,0,0.1);
        }
    </style>
}

@functions {
    private string GetSectionCssClass(string sectionType)
    {
        return sectionType switch
        {
            "featured" => "section-featured",
            "news" => "section-news",
            "testimonials" => "section-testimonials",
            _ => "section-default"
        };
    }

    private async Task<string> GetLocalizedContent(string key)
    {
        // Simulate localization lookup
        await Task.Delay(1);
        return $"Localized: {key}";
    }
}`;

      const layoutCode = `@using Microsoft.AspNetCore.Mvc.TagHelpers
@namespace MyApp.Views.Shared
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
@addTagHelper *, MyApp.TagHelpers

<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"] - MyApp</title>

    <link rel="stylesheet" href="~/lib/bootstrap/dist/css/bootstrap.min.css" />
    <link rel="stylesheet" href="~/css/site.css" asp-append-version="true" />

    @await RenderSectionAsync("MetaTags", required: false)
    @await RenderSectionAsync("Styles", required: false)
</head>
<body>
    <header>
        <nav class="navbar navbar-expand-sm navbar-toggleable-sm navbar-light bg-white border-bottom box-shadow mb-3">
            <div class="container">
                <a class="navbar-brand" asp-controller="Home" asp-action="Index">MyApp</a>

                <div class="navbar-collapse collapse d-sm-inline-flex justify-content-between">
                    <ul class="navbar-nav flex-grow-1">
                        <li class="nav-item">
                            <a class="nav-link text-dark" asp-controller="Home" asp-action="Index">Home</a>
                        </li>
                        <li class="nav-item">
                            <a class="nav-link text-dark" asp-controller="Products" asp-action="Index">Products</a>
                        </li>
                    </ul>

                    <partial name="_LoginPartial" />
                </div>
            </div>
        </nav>
    </header>

    <div class="container">
        <main role="main" class="pb-3">
            @RenderBody()
        </main>
    </div>

    <footer class="border-top footer text-muted">
        <div class="container">
            &copy; @DateTime.Now.Year - MyApp -
            <a asp-controller="Home" asp-action="Privacy">Privacy</a>
        </div>
    </footer>

    <script src="~/lib/jquery/dist/jquery.min.js"></script>
    <script src="~/lib/bootstrap/dist/js/bootstrap.bundle.min.js"></script>
    <script src="~/js/site.js" asp-append-version="true"></script>

    @await RenderSectionAsync("Scripts", required: false)

    <environment include="Development">
        <script src="~/js/debug.js"></script>
    </environment>
</body>
</html>`;

      const result = await parserManager.parseFile('Index.razor', razorCode);
      const extractor = new RazorExtractor('razor', 'Index.razor', razorCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Layout assignment
      const layoutAssignment = symbols.find(s => s.signature?.includes('Layout = "_Layout"'));
      expect(layoutAssignment).toBeDefined();

      // ViewData assignments
      const titleAssignment = symbols.find(s => s.signature?.includes('ViewData["Title"]'));
      expect(titleAssignment).toBeDefined();

      const metaDescription = symbols.find(s => s.signature?.includes('ViewBag.MetaDescription'));
      expect(metaDescription).toBeDefined();

      // Component invocation
      const componentInvoke = symbols.find(s => s.signature?.includes('Component.InvokeAsync("FeaturedProducts"'));
      expect(componentInvoke).toBeDefined();

      // Sections
      const metaTagsSection = symbols.find(s => s.name === 'MetaTags' && s.signature?.includes('@section MetaTags'));
      expect(metaTagsSection).toBeDefined();
      expect(metaTagsSection?.kind).toBe(SymbolKind.Module);

      const scriptsSection = symbols.find(s => s.name === 'Scripts' && s.signature?.includes('@section Scripts'));
      expect(scriptsSection).toBeDefined();

      const stylesSection = symbols.find(s => s.name === 'Styles' && s.signature?.includes('@section Styles'));
      expect(stylesSection).toBeDefined();

      // Functions
      const getSectionCssClass = symbols.find(s => s.name === 'GetSectionCssClass');
      expect(getSectionCssClass).toBeDefined();
      expect(getSectionCssClass?.signature).toContain('private string GetSectionCssClass(string sectionType)');

      const getLocalizedContent = symbols.find(s => s.name === 'GetLocalizedContent');
      expect(getLocalizedContent).toBeDefined();
      expect(getLocalizedContent?.signature).toContain('private async Task<string> GetLocalizedContent(string key)');

      // Test layout parsing separately
      const layoutResult = await parserManager.parseFile('_Layout.cshtml', layoutCode);
      const layoutExtractor = new RazorExtractor('razor', '_Layout.cshtml', layoutCode);
      const layoutSymbols = layoutExtractor.extractSymbols(layoutResult.tree);

      // Layout directives
      const usingDirective = layoutSymbols.find(s => s.name === 'Microsoft.AspNetCore.Mvc.TagHelpers');
      expect(usingDirective).toBeDefined();

      const namespaceDirective = layoutSymbols.find(s => s.name === '@namespace');
      expect(namespaceDirective).toBeDefined();
      expect(namespaceDirective?.signature).toContain('MyApp.Views.Shared');

      const addTagHelper = layoutSymbols.find(s => s.signature?.includes('@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers'));
      expect(addTagHelper).toBeDefined();

      // Render methods
      const renderSectionAsync = layoutSymbols.find(s => s.signature?.includes('RenderSectionAsync("MetaTags"'));
      expect(renderSectionAsync).toBeDefined();

      const renderBody = layoutSymbols.find(s => s.signature?.includes('RenderBody()'));
      expect(renderBody).toBeDefined();
    });
  });

  describe('Razor Data Binding and Events', () => {
    it('should extract two-way binding, event handlers, and form validation', async () => {
      const razorCode = `@page "/contact"
@model ContactFormModel
@inject IEmailService EmailService
@inject IValidator<ContactFormModel> Validator

<div class="contact-form-container">
    <h2>Contact Us</h2>

    <EditForm Model="Model" OnValidSubmit="HandleValidSubmit" OnInvalidSubmit="HandleInvalidSubmit">
        <ObjectGraphDataAnnotationsValidator />
        <ValidationSummary class="text-danger" />

        <div class="form-row">
            <div class="form-group col-md-6">
                <label for="firstName">First Name</label>
                <InputText id="firstName" class="form-control" @bind-Value="Model.FirstName"
                          @bind-Value:event="oninput" placehnewer="Enter first name" />
                <ValidationMessage For="@(() => Model.FirstName)" class="text-danger" />
            </div>

            <div class="form-group col-md-6">
                <label for="lastName">Last Name</label>
                <InputText id="lastName" class="form-control" @bind-Value="Model.LastName"
                          placehnewer="Enter last name" />
                <ValidationMessage For="@(() => Model.LastName)" class="text-danger" />
            </div>
        </div>

        <div class="form-group">
            <label for="email">Email Address</label>
            <InputText id="email" type="email" class="form-control" @bind-Value="Model.Email"
                      @onblur="ValidateEmail" @onfocus="ClearEmailValidation" />
            <ValidationMessage For="@(() => Model.Email)" class="text-danger" />
        </div>

        <div class="form-group">
            <label for="subject">Subject</label>
            <InputSelect id="subject" class="form-control" @bind-Value="Model.Subject"
                        @onchange="HandleSubjectChange">
                <option value="">Select a subject</option>
                <option value="general">General Inquiry</option>
                <option value="support">Technical Support</option>
                <option value="sales">Sales Question</option>
                <option value="feedback">Feedback</option>
            </InputSelect>
            <ValidationMessage For="@(() => Model.Subject)" class="text-danger" />
        </div>

        <div class="form-group">
            <label for="priority">Priority Level</label>
            <InputRadioGroup @bind-Value="Model.Priority" class="priority-group">
                <div class="form-check form-check-inline">
                    <InputRadio Value="@PriorityLevel.Low" id="priorityLow" class="form-check-input" />
                    <label class="form-check-label" for="priorityLow">Low</label>
                </div>
                <div class="form-check form-check-inline">
                    <InputRadio Value="@PriorityLevel.Medium" id="priorityMedium" class="form-check-input" />
                    <label class="form-check-label" for="priorityMedium">Medium</label>
                </div>
                <div class="form-check form-check-inline">
                    <InputRadio Value="@PriorityLevel.High" id="priorityHigh" class="form-check-input" />
                    <label class="form-check-label" for="priorityHigh">High</label>
                </div>
            </InputRadioGroup>
        </div>

        <div class="form-group">
            <div class="form-check">
                <InputCheckbox id="newsletter" class="form-check-input" @bind-Value="Model.SubscribeToNewsletter" />
                <label class="form-check-label" for="newsletter">
                    Subscribe to our newsletter
                </label>
            </div>

            <div class="form-check">
                <InputCheckbox id="terms" class="form-check-input" @bind-Value="Model.AcceptTerms" />
                <label class="form-check-label" for="terms">
                    I accept the <a href="/terms" target="_blank">terms and conditions</a>
                </label>
            </div>
        </div>

        <div class="form-group">
            <label for="message">Message</label>
            <InputTextArea id="message" class="form-control" rows="6" @bind-Value="Model.Message"
                          @oninput="HandleMessageInput" placehnewer="Enter your message..." />
            <ValidationMessage For="@(() => Model.Message)" class="text-danger" />
            <small class="form-text text-muted">
                Character count: @(Model.Message?.Length ?? 0) / @Model.MaxMessageLength
            </small>
        </div>

        <div class="form-group">
            <label for="attachment">Attachment (optional)</label>
            <InputFile id="attachment" class="form-control-file" OnChange="HandleFileSelection"
                      accept=".pdf,.doc,.docx,.txt" multiple />

            @if (SelectedFiles.Any())
            {
                <div class="selected-files mt-2">
                    <h6>Selected Files:</h6>
                    <ul class="list-unstyled">
                        @foreach (var file in SelectedFiles)
                        {
                            <li class="d-flex justify-content-between align-items-center">
                                <span>@file.Name (@file.Size.ToString("N0") bytes)</span>
                                <button type="button" class="btn btn-sm btn-outline-danger"
                                       @onclick="() => RemoveFile(file)">Remove</button>
                            </li>
                        }
                    </ul>
                </div>
            }
        </div>

        <div class="form-actions">
            <button type="submit" class="btn btn-primary" disabled="@(IsSubmitting || !IsFormValid)">
                @if (IsSubmitting)
                {
                    <span class="spinner-border spinner-border-sm" role="status"></span>
                    <span>Sending...</span>
                }
                else
                {
                    <i class="fas fa-paper-plane"></i>
                    <span>Send Message</span>
                }
            </button>

            <button type="button" class="btn btn-secondary ml-2" @onclick="ResetForm">
                Reset Form
            </button>
        </div>
    </EditForm>

    @if (!string.IsNullOrEmpty(SubmissionMessage))
    {
        <div class="alert @(IsSubmissionSuccess ? "alert-success" : "alert-danger") mt-3" role="alert">
            @SubmissionMessage
        </div>
    }
</div>

@code {
    private bool IsSubmitting { get; set; }
    private bool IsFormValid { get; set; }
    private bool IsSubmissionSuccess { get; set; }
    private string? SubmissionMessage { get; set; }
    private List<IBrowserFile> SelectedFiles { get; set; } = new();
    private Timer? validationTimer;

    protected override async Task OnInitializedAsync()
    {
        Model.Priority = PriorityLevel.Medium;
        await ValidateForm();
    }

    private async Task HandleValidSubmit(EditContext editContext)
    {
        IsSubmitting = true;
        StateHasChanged();

        try
        {
            var result = await EmailService.SendContactEmailAsync(Model, SelectedFiles);

            if (result.IsSuccess)
            {
                SubmissionMessage = "Thank you for your message! We'll get back to you soon.";
                IsSubmissionSuccess = true;
                await ResetForm();
            }
            else
            {
                SubmissionMessage = $"Error sending message: {result.ErrorMessage}";
                IsSubmissionSuccess = false;
            }
        }
        catch (Exception ex)
        {
            SubmissionMessage = "An unexpected error occurred. Please try again.";
            IsSubmissionSuccess = false;
        }
        finally
        {
            IsSubmitting = false;
            StateHasChanged();
        }
    }

    private void HandleInvalidSubmit(EditContext editContext)
    {
        SubmissionMessage = "Please correct the errors below and try again.";
        IsSubmissionSuccess = false;
    }

    private async Task ValidateEmail()
    {
        if (!string.IsNullOrEmpty(Model.Email))
        {
            var isValid = await EmailService.ValidateEmailAddressAsync(Model.Email);
            if (!isValid)
            {
                // Add custom validation error
            }
        }
    }

    private void ClearEmailValidation()
    {
        // Clear any custom validation messages
    }

    private async Task HandleSubjectChange(ChangeEventArgs e)
    {
        Model.Subject = e.Value?.ToString();
        await ValidateForm();
    }

    private async Task HandleMessageInput(ChangeEventArgs e)
    {
        Model.Message = e.Value?.ToString();

        // Debounce validation
        validationTimer?.Dispose();
        validationTimer = new Timer(async _ => await ValidateForm(), null, 500, Timeout.Infinite);
    }

    private async Task HandleFileSelection(InputFileChangeEventArgs e)
    {
        SelectedFiles.Clear();

        foreach (var file in e.GetMultipleFiles(maxAllowedFiles: 5))
        {
            if (file.Size <= 10 * 1024 * 1024) // 10MB limit
            {
                SelectedFiles.Add(file);
            }
        }

        StateHasChanged();
    }

    private void RemoveFile(IBrowserFile file)
    {
        SelectedFiles.Remove(file);
        StateHasChanged();
    }

    private async Task ResetForm()
    {
        Model = new ContactFormModel { Priority = PriorityLevel.Medium };
        SelectedFiles.Clear();
        SubmissionMessage = null;
        await ValidateForm();
        StateHasChanged();
    }

    private async Task ValidateForm()
    {
        var validationResult = await Validator.ValidateAsync(Model);
        IsFormValid = validationResult.IsValid && Model.AcceptTerms;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            validationTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}`;

      const result = await parserManager.parseFile('Contact.razor', razorCode);
      const extractor = new RazorExtractor('razor', 'Contact.razor', razorCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Two-way binding
      const firstNameBinding = symbols.find(s => s.signature?.includes('@bind-Value="Model.FirstName"'));
      expect(firstNameBinding).toBeDefined();

      const emailBinding = symbols.find(s => s.signature?.includes('@bind-Value="Model.Email"'));
      expect(emailBinding).toBeDefined();

      // Event binding with custom event
      const inputBinding = symbols.find(s => s.signature?.includes('@bind-Value:event="oninput"'));
      expect(inputBinding).toBeDefined();

      // Event handlers
      const validateEmail = symbols.find(s => s.name === 'ValidateEmail');
      expect(validateEmail).toBeDefined();
      expect(validateEmail?.signature).toContain('private async Task ValidateEmail()');

      const handleSubjectChange = symbols.find(s => s.name === 'HandleSubjectChange');
      expect(handleSubjectChange).toBeDefined();
      expect(handleSubjectChange?.signature).toContain('private async Task HandleSubjectChange(ChangeEventArgs e)');

      const handleFileSelection = symbols.find(s => s.name === 'HandleFileSelection');
      expect(handleFileSelection).toBeDefined();
      expect(handleFileSelection?.signature).toContain('private async Task HandleFileSelection(InputFileChangeEventArgs e)');

      // Form submission handlers
      const handleValidSubmit = symbols.find(s => s.name === 'HandleValidSubmit');
      expect(handleValidSubmit).toBeDefined();
      expect(handleValidSubmit?.signature).toContain('private async Task HandleValidSubmit(EditContext editContext)');

      const handleInvalidSubmit = symbols.find(s => s.name === 'HandleInvalidSubmit');
      expect(handleInvalidSubmit).toBeDefined();

      // Private fields
      const isSubmitting = symbols.find(s => s.name === 'IsSubmitting');
      expect(isSubmitting).toBeDefined();
      expect(isSubmitting?.signature).toContain('private bool IsSubmitting');

      const selectedFiles = symbols.find(s => s.name === 'SelectedFiles');
      expect(selectedFiles).toBeDefined();
      expect(selectedFiles?.signature).toContain('private List<IBrowserFile> SelectedFiles');

      const validationTimer = symbols.find(s => s.name === 'validationTimer');
      expect(validationTimer).toBeDefined();

      // Lifecycle and utility methods
      const onInitializedAsync = symbols.find(s => s.name === 'OnInitializedAsync');
      expect(onInitializedAsync).toBeDefined();

      const resetForm = symbols.find(s => s.name === 'ResetForm');
      expect(resetForm).toBeDefined();

      const validateForm = symbols.find(s => s.name === 'ValidateForm');
      expect(validateForm).toBeDefined();

      const removeFile = symbols.find(s => s.name === 'RemoveFile' && s.kind === SymbolKind.Method);
      expect(removeFile).toBeDefined();
      expect(removeFile?.signature).toContain('private void RemoveFile(IBrowserFile file)');

      // Disposal
      const dispose = symbols.find(s => s.name === 'Dispose');
      expect(dispose).toBeDefined();
      expect(dispose?.signature).toContain('protected override void Dispose(bool disposing)');
    });
  });

  describe('Type Inference and Relationships', () => {
    it('should infer types from Razor code blocks and C# syntax', async () => {
      const razorCode = `@page "/dashboard"
@model DashboardModel
@inject IUserService UserService
@inject ILogger<Dashboard> Logger

@code {
    private bool IsLoading { get; set; } = true;
    private string? ErrorMessage { get; set; }
    private List<UserData> Users { get; set; } = new();
    private Timer? RefreshTimer { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await LoadUsers();
        StartAutoRefresh();
    }

    private async Task LoadUsers()
    {
        try
        {
            IsLoading = true;
            Users = await UserService.GetActiveUsersAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Logger.LogError(ex, "Failed to load users");
        }
        finally
        {
            IsLoading = false;
            StateHasChanged();
        }
    }

    private void StartAutoRefresh()
    {
        RefreshTimer = new Timer(async _ => await LoadUsers(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }
}`;

      const result = await parserManager.parseFile('Dashboard.razor', razorCode);
      const extractor = new RazorExtractor('razor', 'Dashboard.razor', razorCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      // Property types
      const isLoading = symbols.find(s => s.name === 'IsLoading');
      expect(isLoading).toBeDefined();
      expect(types.get(isLoading!.id)).toBe('bool');

      const errorMessage = symbols.find(s => s.name === 'ErrorMessage');
      expect(errorMessage).toBeDefined();
      expect(types.get(errorMessage!.id)).toBe('string?');

      const users = symbols.find(s => s.name === 'Users');
      expect(users).toBeDefined();
      expect(types.get(users!.id)).toBe('List<UserData>');

      const refreshTimer = symbols.find(s => s.name === 'RefreshTimer');
      expect(refreshTimer).toBeDefined();
      expect(types.get(refreshTimer!.id)).toBe('Timer?');

      // Method return types
      const onInitialized = symbols.find(s => s.name === 'OnInitializedAsync');
      expect(onInitialized).toBeDefined();
      expect(types.get(onInitialized!.id)).toBe('Task');

      const loadUsers = symbols.find(s => s.name === 'LoadUsers' && s.kind === SymbolKind.Method);
      expect(loadUsers).toBeDefined();
      expect(types.get(loadUsers!.id)).toBe('Task');

      const startAutoRefresh = symbols.find(s => s.name === 'StartAutoRefresh' && s.kind === SymbolKind.Method);
      expect(startAutoRefresh).toBeDefined();
      expect(types.get(startAutoRefresh!.id)).toBe('void');
    });

    it('should extract component relationships and dependencies', async () => {
      const razorCode = `@inherits LayoutComponentBase
@implements IDisposable
@inject IJSRuntime JSRuntime
@inject IConfiguration Configuration

<div class="app-layout">
    <AppHeader User="@CurrentUser" OnMenuToggle="HandleMenuToggle" />

    <aside class="sidebar @(IsSidebarOpen ? "open" : "closed")">
        <Navigation />
    </aside>

    <main class="main-content">
        @Body

        <AppFooter Version="@AppVersion" />
    </main>

    <ErrorBoundary>
        <ChildContent>
            <NotificationContainer />
        </ChildContent>
        <ErrorContent>
            <div class="error-fallback">
                <h3>Something went wrong</h3>
                <p>Please refresh the page and try again.</p>
            </div>
        </ErrorContent>
    </ErrorBoundary>
</div>

@code {
    [CascadingParameter] public User? CurrentUser { get; set; }

    private bool IsSidebarOpen { get; set; } = true;
    private string AppVersion { get; set; } = "";

    protected override async Task OnInitializedAsync()
    {
        AppVersion = Configuration["AppVersion"] ?? "1.0.0";
        await JSRuntime.InvokeVoidAsync("initializeLayout");
    }

    private void HandleMenuToggle()
    {
        IsSidebarOpen = !IsSidebarOpen;
        StateHasChanged();
    }

    public void Dispose()
    {
        // Cleanup
    }
}`;

      const result = await parserManager.parseFile('AppLayout.razor', razorCode);
      const extractor = new RazorExtractor('razor', 'AppLayout.razor', razorCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should find component usage relationships
      expect(relationships.length).toBeGreaterThanOrEqual(4);

      // Component dependencies (uses relationships)
      const headerUsage = relationships.find(r =>
        r.kind === 'uses' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'AppHeader'
      );
      expect(headerUsage).toBeDefined();

      const navigationUsage = relationships.find(r =>
        r.kind === 'uses' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Navigation'
      );
      expect(navigationUsage).toBeDefined();

      const footerUsage = relationships.find(r =>
        r.kind === 'uses' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'AppFooter'
      );
      expect(footerUsage).toBeDefined();

      const notificationUsage = relationships.find(r =>
        r.kind === 'uses' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'NotificationContainer'
      );
      expect(notificationUsage).toBeDefined();
    });
  });
});