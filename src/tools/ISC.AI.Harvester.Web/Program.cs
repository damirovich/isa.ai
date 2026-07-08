using ISC.AI.Harvester;
using ISC.AI.Harvester.Web.Components;
using ISC.AI.Harvester.Web.Services;
using MudBlazor.Services;

// Веб-консоль оператора сборщика — запускается ВНЕ изолированного контура (ADR-0015).
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

// Движок сбора (экстрактор, запись пакета, коннекторы).
builder.Services.AddHarvesterEngine();

// Фоновый оркестратор массового сбора (стрим в шардированный пакет, прогресс, стоп) — Э4-17.
builder.Services.AddSingleton<HarvestRunner>();

var app = builder.Build();

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
