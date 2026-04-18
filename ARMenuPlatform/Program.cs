using ARMenuPlatform.Helpers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<DbHelper>();
builder.Services.AddScoped<AzureBlobHelper>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 524_288_000;
});
builder.Services.AddDistributedMemoryCache();  // In-memory session store (single server)

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(60); // Session expires after 60 min of inactivity
    options.Cookie.HttpOnly = true;   // JS cannot read the session cookie — prevents XSS theft
    options.Cookie.IsEssential = true;   // Cookie is allowed even if user hasn't accepted cookie policy
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always; // Only sent over HTTPS
    options.Cookie.SameSite = SameSiteMode.Strict;        // Blocks CSRF cross-site sends
});
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseSession();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
