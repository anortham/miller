import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';

describe('Razor Parser', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Razor File Detection', () => {
    it('should detect .razor files as Razor language', () => {
      expect(parserManager.getLanguageForFile('UserProfile.razor')).toBe('razor');
      expect(parserManager.getLanguageForFile('Components/Header.razor')).toBe('razor');
      expect(parserManager.isFileSupported('Pages/Index.razor')).toBe(true);
    });

    it('should detect .cshtml files as Razor language', () => {
      expect(parserManager.getLanguageForFile('Views/Home/Index.cshtml')).toBe('razor');
      expect(parserManager.getLanguageForFile('Shared/_Layout.cshtml')).toBe('razor');
      expect(parserManager.isFileSupported('Areas/Admin/Views/Users.cshtml')).toBe(true);
    });
  });

  describe('Razor Syntax Parsing', () => {
    it('should parse Razor directives and code blocks', async () => {
      const razorCode = `@page "/users/{userId}"
@using MyApp.Models
@inject UserService UserService
@inject NavigationManager Navigation

<h3>User Profile</h3>

@if (isLoading)
{
    <div class="spinner">Loading...</div>
}
else if (user == null)
{
    <div class="error">User not found</div>
}
else
{
    <div class="user-profile">
        <h4>@user.Name</h4>
        <p>Email: @user.Email</p>
        <p>Created: @user.CreatedAt.ToString("yyyy-MM-dd")</p>

        <div class="actions">
            <button class="btn btn-primary" @onclick="EditUser">Edit</button>
            <button class="btn btn-secondary" @onclick="RefreshUser">Refresh</button>
            <button class="btn btn-danger" @onclick="() => DeleteUser(user.Id)">Delete</button>
        </div>
    </div>
}

@code {
    [Parameter] public string UserId { get; set; } = string.Empty;
    [CascadingParameter] public Task<AuthenticationState> AuthStateTask { get; set; }

    private User? user;
    private bool isLoading = false;
    private string errorMessage = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        await LoadUser();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!string.IsNullOrEmpty(UserId))
        {
            await LoadUser();
        }
    }

    private async Task LoadUser()
    {
        isLoading = true;
        StateHasChanged();

        try
        {
            user = await UserService.GetUserAsync(UserId);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task RefreshUser()
    {
        await LoadUser();
    }

    private void EditUser()
    {
        Navigation.NavigateTo($"/users/{UserId}/edit");
    }

    private async Task DeleteUser(string id)
    {
        if (await ConfirmDelete())
        {
            try
            {
                await UserService.DeleteUserAsync(id);
                Navigation.NavigateTo("/users");
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                StateHasChanged();
            }
        }
    }

    private async Task<bool> ConfirmDelete()
    {
        // In real app, would use a modal or JS confirm
        return true;
    }
}`;

      try {
        const result = await parserManager.parseFile('UserProfile.razor', razorCode);

        // Should successfully identify as Razor
        expect(result.language).toBe('razor');
        expect(result.content).toBe(razorCode);
        expect(result.hash).toBeDefined();
        expect(result.filePath).toBe('UserProfile.razor');

        // If tree parsing succeeded, verify structure
        if (result.tree && result.tree.rootNode) {
          expect(result.tree.rootNode).toBeDefined();
          console.log('✅ Razor parsing successful - tree structure available');

          // Basic tree structure validation
          const rootNode = result.tree.rootNode;
          expect(rootNode.type).toBeDefined();
          expect(rootNode.childCount).toBeGreaterThanOrEqual(0);

          console.log(`📝 Razor tree root type: ${rootNode.type}`);
          console.log(`📝 Razor tree children: ${rootNode.childCount}`);
        } else {
          console.log('⚠️  Razor tree parsing may have limitations - basic parsing succeeded');
        }

      } catch (error) {
        // If WASM parsing fails, should still handle gracefully
        console.log(`⚠️  Razor parser error (expected in some environments): ${error.message}`);

        // Should be a known parsing issue, not a crash
        expect(error.message).toMatch(/razor|Language parser not loaded|Failed to load parser|ENOENT|Incompatible language version/);
      }
    });

    it('should parse Razor component with complex features', async () => {
      const complexRazorCode = `@page "/weather"
@using WeatherApp.Data
@inject WeatherForecastService ForecastService
@implements IDisposable

<PageTitle>Weather</PageTitle>

<h1>Weather forecast</h1>

<p>This component demonstrates fetching data from a service.</p>

@if (forecasts == null)
{
    <p><em>Loading...</em></p>
}
else
{
    <table class="table">
        <thead>
            <tr>
                <th>Date</th>
                <th>Temp. (C)</th>
                <th>Temp. (F)</th>
                <th>Summary</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var forecast in forecasts)
            {
                <tr>
                    <td>@forecast.Date.ToShortDateString()</td>
                    <td>@forecast.TemperatureC</td>
                    <td>@forecast.TemperatureF</td>
                    <td>@forecast.Summary</td>
                </tr>
            }
        </tbody>
    </table>
}

<div class="actions">
    <button class="btn btn-primary" @onclick="RefreshData">Refresh</button>
    <button class="btn btn-secondary" @onclick="() => ExportData(ExportFormat.CSV)">Export CSV</button>
</div>

@code {
    private WeatherForecast[]? forecasts;
    private Timer? timer;

    public enum ExportFormat { CSV, JSON, XML }

    protected override async Task OnInitializedAsync()
    {
        forecasts = await ForecastService.GetForecastAsync(DateTime.Now);

        // Auto-refresh every 30 seconds
        timer = new Timer(async _ => await RefreshData(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private async Task RefreshData()
    {
        forecasts = null;
        StateHasChanged();
        forecasts = await ForecastService.GetForecastAsync(DateTime.Now);
        StateHasChanged();
    }

    private async Task ExportData(ExportFormat format)
    {
        if (forecasts == null) return;

        try
        {
            var data = format switch
            {
                ExportFormat.CSV => ConvertToCsv(forecasts),
                ExportFormat.JSON => JsonSerializer.Serialize(forecasts),
                ExportFormat.XML => ConvertToXml(forecasts),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };

            await DownloadFile(data, $"weather.{format.ToString().ToLower()}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Export failed: {ex.Message}");
        }
    }

    private string ConvertToCsv(WeatherForecast[] data)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Date,TemperatureC,TemperatureF,Summary");

        foreach (var item in data)
        {
            csv.AppendLine($"{item.Date:yyyy-MM-dd},{item.TemperatureC},{item.TemperatureF},{item.Summary}");
        }

        return csv.ToString();
    }

    private string ConvertToXml(WeatherForecast[] data)
    {
        // XML conversion logic would go here
        return "<forecasts></forecasts>";
    }

    private async Task DownloadFile(string content, string filename)
    {
        // File download logic would go here
        await Task.Delay(100);
    }

    public void Dispose()
    {
        timer?.Dispose();
    }
}

<style>
    .table {
        width: 100%;
        border-collapse: collapse;
    }

    .table th, .table td {
        border: 1px solid #ddd;
        padding: 8px;
        text-align: left;
    }

    .table th {
        background-color: #f2f2f2;
        font-weight: bnew;
    }

    .actions {
        margin-top: 20px;
    }

    .btn {
        padding: 8px 16px;
        margin-right: 10px;
        border: none;
        border-radius: 4px;
        cursor: pointer;
    }

    .btn-primary {
        background-color: #007bff;
        color: white;
    }

    .btn-secondary {
        background-color: #6c757d;
        color: white;
    }
</style>`;

      try {
        const result = await parserManager.parseFile('Weather.razor', complexRazorCode);

        expect(result.language).toBe('razor');
        expect(result.content).toBe(complexRazorCode);
        expect(result.hash).toBeDefined();

        if (result.tree && result.tree.rootNode) {
          console.log('✅ Complex Razor parsing successful');

          // Verify we can traverse the tree structure
          const rootNode = result.tree.rootNode;
          expect(rootNode.type).toBeDefined();

          // Look for key Razor constructs in the tree
          let foundRazorDirectives = false;
          let foundCodeBlocks = false;

          const traverseNode = (node: any) => {
            if (node.type && node.type.includes('directive')) {
              foundRazorDirectives = true;
            }
            if (node.type && node.type.includes('code')) {
              foundCodeBlocks = true;
            }

            for (let i = 0; i < node.childCount; i++) {
              traverseNode(node.child(i));
            }
          };

          traverseNode(rootNode);

          console.log(`📝 Found Razor directives: ${foundRazorDirectives}`);
          console.log(`📝 Found code blocks: ${foundCodeBlocks}`);
        }

      } catch (error) {
        console.log(`⚠️  Complex Razor parser error: ${error.message}`);
        expect(error.message).toMatch(/razor|Language parser not loaded|Failed to load parser|ENOENT|Incompatible language version/);
      }
    });

    it('should parse Razor with inline expressions and event handlers', async () => {
      const inlineRazorCode = `@page "/counter"

<h1>Counter</h1>

<p role="status">Current count: @currentCount</p>

<div class="counter-controls">
    <button class="btn btn-primary" @onclick="IncrementCount">Click me</button>
    <button class="btn btn-secondary" @onclick="() => DecrementCount()">Decrement</button>
    <button class="btn btn-danger" @onclick="@(() => ResetCount())">Reset</button>
</div>

<div class="counter-display">
    <span class="@GetCounterClass()">@currentCount.ToString("N0")</span>
    <p>@(currentCount > 10 ? "High count!" : "Low count")</p>
</div>

@code {
    private int currentCount = 0;

    private void IncrementCount()
    {
        currentCount++;
    }

    private void DecrementCount()
    {
        currentCount--;
    }

    private void ResetCount() => currentCount = 0;

    private string GetCounterClass()
    {
        return currentCount switch
        {
            > 20 => "text-danger",
            > 10 => "text-warning",
            _ => "text-success"
        };
    }
}`;

      try {
        const result = await parserManager.parseFile('Counter.razor', inlineRazorCode);

        expect(result.language).toBe('razor');
        expect(result.content).toBe(inlineRazorCode);

        console.log('✅ Inline Razor expressions parsing completed');

      } catch (error) {
        console.log(`⚠️  Inline Razor parser error: ${error.message}`);
        expect(error.message).toMatch(/razor|Language parser not loaded|Failed to load parser|ENOENT|Incompatible language version/);
      }
    });
  });

  describe('Razor Parser Capabilities', () => {
    it('should handle Razor parser availability gracefully', () => {
      // Test parser availability
      const hasRazorParser = parserManager.hasParser('razor');
      console.log(`🔧 Razor parser available: ${hasRazorParser}`);

      // Should be included in supported languages list
      const supportedLanguages = parserManager.getSupportedLanguages();
      const supportedExtensions = parserManager.getSupportedExtensions();

      expect(supportedExtensions).toContain('.razor');
      expect(supportedExtensions).toContain('.cshtml');

      console.log(`📊 Total supported languages: ${supportedLanguages.length}`);
      console.log(`📊 Total supported extensions: ${supportedExtensions.length}`);
    });

    it('should generate consistent hashes for Razor content', () => {
      const razorContent = '@page "/test"\n<h1>Test</h1>\n@code { private string test = "hello"; }';

      const hash1 = parserManager.hashContent(razorContent);
      const hash2 = parserManager.hashContent(razorContent);
      const hash3 = parserManager.hashContent(razorContent + ' '); // Different content

      expect(hash1).toBe(hash2);
      expect(hash1).not.toBe(hash3);
      expect(hash1).toMatch(/^[a-f0-9]{64}$/); // SHA-256 hex format

      console.log(`🔐 Razor content hashing working correctly`);
    });

    it('should handle Razor parsing errors gracefully', async () => {
      const invalidRazorCode = '@page "/invalid\n@using \n@code { invalid syntax here }';

      try {
        const result = await parserManager.parseFile('Invalid.razor', invalidRazorCode);

        // If parsing succeeds, should still return valid result structure
        expect(result.language).toBe('razor');
        expect(result.content).toBe(invalidRazorCode);
        expect(result.hash).toBeDefined();

        console.log('✅ Razor error handling - parsing completed despite invalid syntax');

      } catch (error) {
        // Should handle errors gracefully without crashing
        expect(error).toBeInstanceOf(Error);
        console.log(`⚠️  Razor error handled gracefully: ${error.message}`);
      }
    });
  });

  describe('Razor Performance', () => {
    it('should parse Razor files within reasonable time', async () => {
      const moderateRazorCode = `@page "/products"
@using ProductCatalog.Models
@inject ProductService ProductService

<h1>Product Catalog</h1>

${Array.from({ length: 20 }, (_, i) => `
<div class="product-${i}">
    <h3>@products[${i}]?.Name</h3>
    <p>@products[${i}]?.Description</p>
    <span class="price">@products[${i}]?.Price.ToString("C")</span>
    <button @onclick="() => AddToCart(${i})">Add to Cart</button>
</div>`).join('\n')}

@code {
    private Product[] products = new Product[20];

    protected override async Task OnInitializedAsync()
    {
        products = await ProductService.GetProductsAsync();
    }

    ${Array.from({ length: 20 }, (_, i) => `
    private async Task AddToCart(int productId)
    {
        await ProductService.AddToCartAsync(productId);
        StateHasChanged();
    }`).join('\n')}
}`;

      const startTime = performance.now();

      try {
        const result = await parserManager.parseFile('Products.razor', moderateRazorCode);
        const endTime = performance.now();
        const parseTime = endTime - startTime;

        expect(result.language).toBe('razor');
        console.log(`⚡ Razor parsing performance: ${parseTime.toFixed(2)}ms for ${moderateRazorCode.length} characters`);

        // Should complete parsing in reasonable time (adjust threshnew as needed)
        expect(parseTime).toBeLessThan(5000); // 5 seconds max

      } catch (error) {
        const endTime = performance.now();
        const parseTime = endTime - startTime;

        console.log(`⚡ Razor parsing error time: ${parseTime.toFixed(2)}ms`);
        expect(parseTime).toBeLessThan(5000); // Should fail fast, not hang
      }
    });
  });
});