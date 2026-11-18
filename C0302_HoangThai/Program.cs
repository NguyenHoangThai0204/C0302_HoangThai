using C0302_HoangThai.Models.M0302;
using C0302_HoangThai.Service.S0302;
using C0302_HoangThai.Service.S0302.IS;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;

var builder = WebApplication.CreateBuilder(args);
if (int.TryParse(builder.Configuration["PORT"], out int port) && port is > 0 and <= 65535)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton<PdfOcr>();
builder.Services.AddSingleton<PdfJobs>();
builder.Services.AddHostedService(services => services.GetRequiredService<PdfJobs>());
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SpreadsheetDownloads>();

// Add services to the container.
builder.Services.AddControllersWithViews()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

// Đăng ký DbContext với connection string
builder.Services.AddDbContext<Context0302>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Connection")));

// Cấu hình Session
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(20);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddScoped<IS0302XoaGoiChiDinhInterface, S0302XoaGoiChiDinhService>();

builder.Services.Configure<IISServerOptions>(options =>
{
    options.MaxRequestBodySize = 500_000_000; // 500 MB
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 500_000_000; // 500 MB
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(10);
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

builder.Services.AddHttpContextAccessor();

// Cấu hình CultureInfo cho ứng dụng
var cultureInfo = new CultureInfo("en-US");
cultureInfo.DateTimeFormat.ShortDatePattern = "dd-MM-yyyy";
cultureInfo.DateTimeFormat.DateSeparator = "-";
CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

var app = builder.Build();
if (app.Configuration.GetValue<bool>("Ocr:ValidateOnStartup"))
    app.Services.GetRequiredService<PdfOcr>().Validate();

// Configure the HTTP request pipeline.
bool behindRender = app.Configuration.GetValue<bool>("RENDER");
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    if (!behindRender)
        app.UseHsts();
}

if (!behindRender)
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();       // ✅ TRƯỚC UseAuthorization
app.UseAuthorization(); // ✅ SAU UseSession

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
