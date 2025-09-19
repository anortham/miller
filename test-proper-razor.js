
import { CodeIntelligenceEngine } from "./src/engine/code-intelligence.js";
import { MillerPaths } from "./src/utils/miller-paths.js";
import { initializeLogger } from "./src/utils/logger.js";
import { writeFileSync } from "fs";

async function testProperRazor() {
  try {
    const paths = new MillerPaths("/tmp");
    await paths.ensureDirectories();
    initializeLogger(paths);

    const engine = new CodeIntelligenceEngine({ workspacePath: "/tmp" });
    await engine.initialize();

    // Create a comprehensive Razor test file
    writeFileSync("/tmp/UserProfile.razor", `
@page "/users/{userId}"
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
}
    `);

    console.log("🧪 Testing proper Razor parser...");
    await engine.indexWorkspace("/tmp");

    // Search for Razor-specific elements
    const pageResults = await engine.searchCode("@page", { limit: 5 });
    const injectResults = await engine.searchCode("@inject", { limit: 5 });
    const codeResults = await engine.searchCode("@code", { limit: 5 });
    const onclickResults = await engine.searchCode("@onclick", { limit: 5 });
    const parameterResults = await engine.searchCode("[Parameter]", { limit: 5 });

    console.log(`🔍 Razor directive results:`);
    console.log(`  @page: ${pageResults.length} results`);
    console.log(`  @inject: ${injectResults.length} results`);
    console.log(`  @code: ${codeResults.length} results`);
    console.log(`  @onclick: ${onclickResults.length} results`);
    console.log(`  [Parameter]: ${parameterResults.length} results`);

    // Search for C# method names in @code block
    const loadUserResults = await engine.searchCode("LoadUser", { limit: 5 });
    const refreshResults = await engine.searchCode("RefreshUser", { limit: 5 });
    const deleteResults = await engine.searchCode("DeleteUser", { limit: 5 });

    console.log(`🔍 C# method results:`);
    console.log(`  LoadUser: ${loadUserResults.length} results`);
    console.log(`  RefreshUser: ${refreshResults.length} results`);
    console.log(`  DeleteUser: ${deleteResults.length} results`);

    await engine.dispose();

    console.log("✅ Proper Razor parser test completed!");
    console.log("🎯 Razor-specific syntax should be parsed correctly!");

  } catch (error) {
    console.error("❌ Razor test failed:", error.message);
  }
}

testProperRazor();

