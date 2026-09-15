using MapStitcher.Business.Contracts;
using MapStitcher.Business.Services;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;
using MapStitcher.Repositories.Repositories;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(
        connectionString,
        new MySqlServerVersion(new Version(8, 0, 21)),
        x => x.UseNetTopologySuite()
    ));

builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<ISurveySheetRepository, SurveySheetRepository>();
builder.Services.AddScoped<ITiePointRepository, TiePointRepository>();
builder.Services.AddScoped<ISheetBoundaryRepository, SheetBoundaryRepository>();
builder.Services.AddScoped<ICadastralMergeService, CadastralMergeService>();
builder.Services.AddScoped<ISheetPlacementService, SheetPlacementService>();
builder.Services.AddScoped<IStitchOrchestrationService, StitchOrchestrationService>();
builder.Services.AddScoped<ICadastralExportService, CadastralExportService>();
builder.Services.AddScoped<ISvgMosaicExportService, SvgMosaicExportService>();
builder.Services.AddScoped<ICadastralParsingService>(sp =>
{
    var sheetRepo = sp.GetRequiredService<ISurveySheetRepository>();
    var tiePointRepo = sp.GetRequiredService<ITiePointRepository>();
    var boundaryRepo = sp.GetRequiredService<ISheetBoundaryRepository>();
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    return new CadastralParsingService(sheetRepo, tiePointRepo, boundaryRepo, env.WebRootPath);
});

builder.Services.AddScoped<IJigsawIndexExtractionService>(sp =>
{
    var sheetRepo = sp.GetRequiredService<ISurveySheetRepository>();
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    return new JigsawIndexExtractionService(sheetRepo, env.WebRootPath);
});

builder.Services.AddScoped<IJigsawStitchService>(sp =>
{
    var sheetRepo = sp.GetRequiredService<ISurveySheetRepository>();
    var indexService = sp.GetRequiredService<IJigsawIndexExtractionService>();
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    return new JigsawStitchService(sheetRepo, indexService, env.WebRootPath);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();