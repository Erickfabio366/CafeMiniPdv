using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não encontrada.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
builder.Services.AddDataProtection().PersistKeysToDbContext<AppDbContext>();

// ==========================================
// AUTENTICAÇÃO COM COOKIE + GOOGLE OAUTH
// ==========================================
var googleClientId = builder.Configuration["Authentication:Google:ClientId"] ?? "";
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? "";
var emailsAutorizados = (builder.Configuration["Seguranca:EmailsAutorizados"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "CafeMiniPdv_Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
})
.AddGoogle(options =>
{
    options.ClientId = googleClientId;
    options.ClientSecret = googleClientSecret;
    options.CallbackPath = "/signin-google";
});

builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseForwardedHeaders();

// Garante que o arquivo cafepdv.db e a tabela Vendas sejam criados se não existirem
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Permite que o ASP.NET entregue o index.html da pasta wwwroot / Arquivos estáticos (ícone, manifest, etc.)
app.UseStaticFiles();

// Ativação dos middlewares de autenticação e autorização
app.UseAuthentication();
app.UseAuthorization();

// ==========================================
// CONFIGURAÇÕES DO SISTEMA
// ==========================================
var chavePix = builder.Configuration["ConfiguracoesPix:ChavePix"] ?? "SUA_CHAVE";
var nomeBeneficiario = builder.Configuration["ConfiguracoesPix:NomeBeneficiario"] ?? "SEU NOME";
var cidade = builder.Configuration["ConfiguracoesPix:Cidade"] ?? "SUA CIDADE";
var precoUnitario = builder.Configuration.GetValue<decimal>("ConfiguracoesPix:PrecoUnitario", 15.00m);

// Controle em memória contra Brute Force
var tentativasFalhas = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Tentativas, DateTime BloqueadoAte)>();

// ==========================================
// ROTAS DE TELA E AUTENTICAÇÃO
// ==========================================

// Rota principal: Entrega Login se não autenticado; Balcão se autenticado
app.MapGet("/", (HttpContext context) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        return Results.Content(GerarHtmlLoginGoogle(), "text/html");
    }

    var html = File.ReadAllText("wwwroot/index.html");
    html = html.Replace("{{PRECO_UNITARIO}}", precoUnitario.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    return Results.Content(html, "text/html");
});

app.MapGet("/login-google", () =>
{
    var authProps = new AuthenticationProperties
    {
        RedirectUri = "/validar-acesso",
        IsPersistent = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
    };
    return Results.Challenge(authProps, new[] { GoogleDefaults.AuthenticationScheme });
});

app.MapGet("/validar-acesso", async (HttpContext context) =>
{
    var emailUsuario = context.User.FindFirst(ClaimTypes.Email)?.Value;

    if (string.IsNullOrEmpty(emailUsuario) || !emailsAutorizados.Contains(emailUsuario))
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        return Results.Content($@"
            <!DOCTYPE html>
            <html lang='pt-BR'>
            <head><meta charset='UTF-8'><title>Acesso Negado</title></head>
            <body style='font-family:sans-serif; background:#f4f4f9; display:flex; align-items:center; justify-content:center; min-height:100vh;'>
                <div style='background:white; padding:24px; border-radius:12px; text-align:center; max-width:320px; box-shadow:0 4px 10px rgba(0,0,0,0.1);'>
                    <h2 style='color:#c62828;'>🚫 Acesso Negado</h2>
                    <p style='color:#555; font-size:0.9rem;'>A conta <b>{emailUsuario}</b> não tem autorização para operar este caixa.</p>
                    <a href='/' style='display:inline-block; margin-top:10px; color:#2e7d32; font-weight:bold;'>Voltar</a>
                </div>
            </body>
            </html>
        ", "text/html");
    }

    return Results.Redirect("/");
}).RequireAuthorization();

// Logout
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    context.Response.Headers.Append("HX-Refresh", "true");
    return Results.Ok();
}).RequireAuthorization();

// ==========================================
// ROTAS DE OPERAÇÃO DO CAIXA
// ==========================================

app.MapGet("/api/admin/dashboard", async (string? periodo, string? forma, string? status, string? busca, AppDbContext db) =>
{
    var htmlDashboard = await GerarHtmlDashboardCompleto(db, periodo, forma, status, busca);
    return Results.Content(htmlDashboard, "text/html");
}).RequireAuthorization();

app.MapPost("/api/vender/pix", async (HttpRequest request, AppDbContext db) =>
{
    var form = await request.ReadFormAsync();
    var (qtd, desconto, valorTotal) = ProcessarValores(form);

    var novaVenda = new Venda
    {
        DataHora = DateTime.Now,
        Quantidade = qtd,
        Desconto = desconto,
        Valor = valorTotal,
        FormaPagamento = "PIX",
        Status = "PENDENTE"
    };

    db.Vendas.Add(novaVenda);
    await db.SaveChangesAsync();

    string txtId = $"PEDIDO{novaVenda.Id}";
    string pixPayload = GeradorPix.CriarPayload(chavePix, nomeBeneficiario, cidade, valorTotal, txtId);

    using var qrGenerator = new QRCodeGenerator();
    using var qrCodeData = qrGenerator.CreateQrCode(pixPayload, QRCodeGenerator.ECCLevel.Q);
    using var qrCode = new PngByteQRCode(qrCodeData);
    byte[] qrCodeBytes = qrCode.GetGraphic(20);
    string base64Image = Convert.ToBase64String(qrCodeBytes);

    string infoDesconto = desconto > 0 ? $"<small style='color:#e65100;'>(Desc: -R$ {desconto:F2})</small>" : "";

    var htmlResposta = $@"
        <div id='bloco-pedido-{novaVenda.Id}' style='margin-top: 15px;'>
            <p style='color: #2e7d32; font-weight: bold; margin-bottom: 8px;'>
                Pedido #{novaVenda.Id} • {qtd} {(qtd > 1 ? "Cafés" : "Café")} • R$ {valorTotal:F2}{infoDesconto}
            </p>
            <img src='data:image/png;base64,{base64Image}' 
                 alt='QR Code Pix' 
                 style='width: 190px; height: 190px; border-radius: 8px; border: 1px solid #ddd;' />
            
            <div style='margin-top: 10px; display: flex; flex-direction: column; gap: 8px;'>
                <button type='button' 
                        onclick=""navigator.clipboard.writeText('{pixPayload}'); alert('Código Pix Copiado!');"" 
                        class='btn-secundario'>
                    📋 Copiar Código Pix
                </button>
                
                <button hx-post='/api/vendas/confirmar/{novaVenda.Id}' 
                        hx-target='#bloco-pedido-{novaVenda.Id}' 
                        hx-swap='outerHTML' 
                        class='btn-confirmar'>
                    ✅ Confirmar Pagamento
                </button>
            </div>
        </div>
    ";

    return Results.Content(htmlResposta, "text/html");
}).RequireAuthorization();

app.MapPost("/api/vender/direto/{tipo}", async (string tipo, HttpRequest request, AppDbContext db) =>
{
    var forma = tipo.ToUpper() == "DINHEIRO" ? "DINHEIRO" : "CARTAO";
    var form = await request.ReadFormAsync();
    var (qtd, desconto, valorTotal) = ProcessarValores(form);

    var novaVenda = new Venda
    {
        DataHora = DateTime.Now,
        Quantidade = qtd,
        Desconto = desconto,
        Valor = valorTotal,
        FormaPagamento = forma,
        Status = "PAGO"
    };

    db.Vendas.Add(novaVenda);
    await db.SaveChangesAsync();

    string infoDesconto = desconto > 0 ? $" (-R$ {desconto:F2})" : "";

    var htmlSucesso = $@"
        <div class='aviso-sucesso' onanimationend='this.remove()'>
            <p style='color: #2e7d32; font-weight: bold; font-size: 1.05rem; margin: 0;'>🎉 Venda #{novaVenda.Id} Concluída!</p>
            <p style='margin: 4px 0 0; color: #444; font-size: 0.9rem;'>
                {qtd} {(qtd > 1 ? "cafés" : "café")} • R$ {valorTotal:F2}{infoDesconto} via <b>{forma}</b>
            </p>
        </div>
    ";

    return Results.Content(htmlSucesso, "text/html");
}).RequireAuthorization();

app.MapPost("/api/vendas/confirmar/{id:int}", async (int id, AppDbContext db) =>
{
    var venda = await db.Vendas.FindAsync(id);
    if (venda is null)
    {
        return Results.Content("<p style='color: red;'>Pedido não encontrado.</p>", "text/html");
    }

    venda.Status = "PAGO";
    await db.SaveChangesAsync();

    var htmlSucesso = $@"
        <div class='aviso-sucesso' onanimationend='this.remove()'>
            <p style='color: #2e7d32; font-weight: bold; font-size: 1.05rem; margin: 0;'>🎉 Pagamento Confirmado!</p>
            <p style='margin: 4px 0 0; color: #444; font-size: 0.9rem;'>Pedido #{venda.Id} ({venda.Quantidade} {(venda.Quantidade > 1 ? "cafés" : "café")}) marcado como <b>PAGO</b></p>
        </div>
    ";

    return Results.Content(htmlSucesso, "text/html");
}).RequireAuthorization();

app.MapPost("/api/vendas/confirmar-admin/{id:int}", async (int id, HttpRequest request, AppDbContext db) =>
{
    var venda = await db.Vendas.FindAsync(id);
    if (venda != null)
    {
        venda.Status = "PAGO";
        await db.SaveChangesAsync();
    }

    var form = await request.ReadFormAsync();
    var html = await GerarHtmlDashboardCompleto(db, form["periodo"], form["forma"], form["status"], form["busca"]);
    return Results.Content(html, "text/html");
}).RequireAuthorization();

app.MapPost("/api/vendas/cancelar/{id:int}", async (int id, HttpRequest request, AppDbContext db) =>
{
    var venda = await db.Vendas.FindAsync(id);
    if (venda != null)
    {
        venda.Status = "CANCELADO";
        await db.SaveChangesAsync();
    }

    var form = await request.ReadFormAsync();
    var html = await GerarHtmlDashboardCompleto(db, form["periodo"], form["forma"], form["status"], form["busca"]);
    return Results.Content(html, "text/html");
}).RequireAuthorization();

app.Run();

// ==========================================
// FUNÇÕES AUXILIARES E HTML EMBUTIDO
// ==========================================

(int qtd, decimal desconto, decimal total) ProcessarValores(IFormCollection form)
{
    int qtd = int.TryParse(form["quantidade"], out var q) ? Math.Max(1, q) : 1;

    string descStr = form["desconto"].ToString().Replace(',', '.');
    decimal.TryParse(descStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var desc);
    desc = Math.Max(0, desc);

    decimal subtotal = qtd * precoUnitario;
    decimal total = Math.Max(0.01m, subtotal - desc);

    return (qtd, desc, total);
}

string GerarHtmlLoginGoogle()
{
    return @"<!DOCTYPE html>
        <html lang='pt-BR'>
        <head>
            <meta charset='UTF-8'>
            <meta name='viewport' content='width=device-width, initial-scale=1.0'>
            <title>Acesso - Mini PDV</title>
            <style>
                body { font-family: -apple-system, BlinkMacSystemFont, sans-serif; background: #f4f4f9; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; padding: 16px; }
                .card { background: white; padding: 32px 24px; border-radius: 16px; box-shadow: 0 4px 12px rgba(0,0,0,0.08); width: 100%; max-width: 320px; text-align: center; }
                .btn-google {
                    display: flex; align-items: center; justify-content: center; gap: 10px;
                    background: white; color: #3c4043; border: 1px solid #dadce0; border-radius: 8px;
                    padding: 12px; font-size: 0.95rem; font-weight: 500; text-decoration: none;
                    box-shadow: 0 1px 3px rgba(0,0,0,0.08); transition: background-color 0.2s;
                }
                .btn-google:hover { background: #f8f9fa; border-color: #c6c9cc; }
            </style>
        </head>
        <body>
            <div class='card'>
                <h2 style='margin-top:0; color:#333;'>☕ Mini PDV</h2>
                <p style='color: #666; font-size: 0.85rem; margin-bottom: 24px;'>Acesso restrito a operadores autorizados.</p>
                <a href='/login-google' class='btn-google'>
                    <svg width='18' height='18' viewBox='0 0 24 24'><path fill='#4285F4' d='M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z'/><path fill='#34A853' d='M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z'/><path fill='#FBBC05' d='M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.06H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.94l2.85-2.22.81-.63z'/><path fill='#EA4335' d='M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.06l3.66 2.84c.87-2.6 3.3-4.52 6.16-4.52z'/></svg>
                    Entrar com Google
                </a>
            </div>
        </body>
        </html>
    ";
}

async Task<string> GerarHtmlDashboardCompleto(AppDbContext db,
                                              string? periodo="hoje",
                                              string? forma="todos",
                                              string? status="todos",
                                              string? busca="")
{
    periodo ??= "hoje";
    forma ??= "todos";
    status ??= "todos";
    busca ??= "";

    var query = db.Vendas.AsQueryable();
    var hoje = DateTime.Today;

    if(periodo == "hoje")
    {
        query = query.Where(v => v.DataHora >= hoje);
    } else if(periodo == "ontem")
    {
        var ontem = hoje.AddDays(-1);
        query = query.Where(v => v.DataHora >= ontem && v.DataHora < hoje);
    } else if(periodo == "7dias")
    {
        var limite = hoje.AddDays(-6);
        query = query.Where(v => v.DataHora >= limite);
    } else if(periodo == "mes")
    {
        var primeiroDiaMes = new DateTime(hoje.Year, hoje.Month, 1);
        query = query.Where(v => v.DataHora >= primeiroDiaMes);
    }

    if(forma != "todos")
    {
        query = query.Where(v => v.FormaPagamento == forma.ToUpper());
    }

    if(status != "todos")
    {
        query = query.Where(v => v.Status == status.ToUpper());
    }

    if(!string.IsNullOrWhiteSpace(busca) && int.TryParse(busca.Replace('#', ' ').Trim(), out int idBuscado))
    {
        query = query.Where(v => v.Id == idBuscado);
    }

    var vendasFiltradas = await query
        .OrderByDescending(v => v.Id)
        .ToListAsync();

    var vendasPagas = vendasFiltradas.Where(v => v.Status == "PAGO").ToList();
    var totalFaturado = vendasPagas.Sum(v => v.Valor);
    var totalDescontos = vendasPagas.Sum(v => v.Desconto);
    var qtdPedidos = vendasPagas.Count;
    var qtdCafes = vendasPagas.Sum(v => v.Quantidade);

    var pedidosPix = vendasPagas.Count(v => v.FormaPagamento == "PIX");
    var pedidosCartao = vendasPagas.Count(v => v.FormaPagamento == "CARTAO");
    var pedidosDinheiro = vendasPagas.Count(v => v.FormaPagamento == "DINHEIRO");

    var sb = new StringBuilder();

    sb.Append($@"
        <div class='card'>
            <h1>📊 Resumo Financeiro</h1>
            <div style='display: flex; justify-content: space-between; font-weight: bold; color: #333;'>
                <span>Pedidos Fechados:</span>
                <span>{qtdPedidos} {(qtdPedidos == 1 ? "pedido" : "pedidos")}</span>
            </div>
            <div style='display: flex; justify-content: space-between; font-weight: bold; color: #4b6584; margin-top: 4px;'>
                <span>Cafés Vendidos:</span>
                <span>{qtdCafes} {(qtdCafes == 1 ? "unidade" : "unidades")}</span>
            </div>
            <div style='display: flex; justify-content: space-between; font-weight: bold; color: #2e7d32; margin-top: 6px; font-size: 1.15rem;'>
                <span>Faturamento:</span>
                <span>R$ {totalFaturado:F2}</span>
            </div>
            {(totalDescontos > 0 ? $@"
            <div style='display: flex; justify-content: space-between; color: #e65100; font-size: 0.85rem; margin-top: 2px;'>
                <span>Descontos Concedidos:</span>
                <span>- R$ {totalDescontos:F2}</span>
            </div>" : "")}
            <hr style='border: 0; border-top: 1px dashed #ddd; margin: 10px 0;'>
            <div style='display: flex; justify-content: space-between; color: #666; font-size: 0.85rem;'>
                <span>• Pix: {pedidosPix}</span>
                <span>• Cartão: {pedidosCartao}</span>
                <span>• Dinheiro: {pedidosDinheiro}</span>
            </div>
        </div>

        <!-- CARD 2: FILTROS & PESQUISA -->
        <div class='card'>

            <h2>🔍 Filtros & Histórico</h2>
    
            <form id='form-filtros' 
                  hx-get='/api/admin/dashboard' 
                  hx-target='#painel-conteudo' 
                  hx-trigger='change, keyup delay:400ms from:#campo-busca'
                  style='margin-bottom: 15px; display: flex; flex-direction: column; gap: 8px;'>
        
                <input type='text' 
                       id='campo-busca'
                       name='busca' 
                       value='{busca}' 
                       placeholder='Pesquisar por #ID do pedido...' 
                       style='width: 100%; box-sizing: border-box; padding: 8px 10px; border: 1px solid #ccc; border-radius: 6px; font-size: 0.85rem;'>

                <div style='display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 5px; width: 100%; box-sizing: border-box;'>
                    <select name='periodo' style='width: 100%; min-width: 0; box-sizing: border-box; padding: 6px 2px; border: 1px solid #ccc; border-radius: 6px; font-size: 0.76rem;'>
                        <option value='hoje' {(periodo == "hoje" ? "selected" : "")}>📅 Hoje</option>
                        <option value='ontem' {(periodo == "ontem" ? "selected" : "")}>📅 Ontem</option>
                        <option value='7dias' {(periodo == "7dias" ? "selected" : "")}>📅 7 dias</option>
                        <option value='mes' {(periodo == "mes" ? "selected" : "")}>📅 Este Mês</option>
                        <option value='todos' {(periodo == "todos" ? "selected" : "")}>📅 Todos</option>
                    </select>

                    <select name='forma' style='width: 100%; min-width: 0; box-sizing: border-box; padding: 6px 2px; border: 1px solid #ccc; border-radius: 6px; font-size: 0.76rem;'>
                        <option value='todos' {(forma == "todos" ? "selected" : "")}>💰 Formas</option>
                        <option value='PIX' {(forma == "PIX" ? "selected" : "")}>🟢 Pix</option>
                        <option value='CARTAO' {(forma == "CARTAO" ? "selected" : "")}>💳 Cartão</option>
                        <option value='DINHEIRO' {(forma == "DINHEIRO" ? "selected" : "")}>💵 Dinheiro</option>
                    </select>

                    <select name='status' style='width: 100%; min-width: 0; box-sizing: border-box; padding: 6px 2px; border: 1px solid #ccc; border-radius: 6px; font-size: 0.76rem;'>
                        <option value='todos' {(status == "todos" ? "selected" : "")}>📌 Status</option>
                        <option value='PAGO' {(status == "PAGO" ? "selected" : "")}>✅ Pagos</option>
                        <option value='PENDENTE' {(status == "PENDENTE" ? "selected" : "")}>⏳ Pendentes</option>
                        <option value='CANCELADO' {(status == "CANCELADO" ? "selected" : "")}>❌ Cancelados</option>
                    </select>
                </div>
            </form>

        <div style='display: flex; flex-direction: column; gap: 8px;'>
    ");

    if (!vendasFiltradas.Any())
    {
        sb.Append("<p style='color: #888; font-size: 0.85rem; text-align: center; margin: 16px 0;'>Nenhuma transação encontrada com esses filtros.</p>");
    } else
    {
        foreach (var v in vendasFiltradas)
        {
            bool isPago = v.Status == "PAGO";
            bool isPendente = v.Status == "PENDENTE";
            bool isCancelado = v.Status == "CANCELADO";
            string corStatus = isPago ? "#2e7d32" : (isCancelado ? "#c62828" : "#f57c00");
            string badgeForma = v.FormaPagamento switch
            {
                "PIX" => "🟢 Pix",
                "CARTAO" => "💳 Cartão",
                "DINHEIRO" => "💵 Dinheiro",
                _ => v.FormaPagamento
            };

            string botoesAcao;
            if (isPago)
            {
                botoesAcao = $@"
                    <button hx-post='/api/vendas/cancelar/{v.Id}' 
                            hx-confirm='Deseja estornar a venda #{v.Id} (R$ {v.Valor:F2})?' 
                            hx-include='#form-filtros'
                            hx-target='#painel-conteudo'
                            style='background: #ffebee; border: 1px solid #ffcdd2; color: #c62828; padding: 4px 8px; border-radius: 4px; font-size: 0.75rem; font-weight: bold; cursor: pointer;'>
                        ✕ Cancelar
                    </button>
                ";
            } else if (isPendente)
            {
                botoesAcao = $@"
                    <div style='display: flex; gap: 4px;'>
                        <button hx-post='/api/vendas/confirmar-admin/{v.Id}' 
                                hx-include='#form-filtros'
                                hx-target='#painel-conteudo'
                                title='Confirmar recebimento do Pix'
                                style='background: #e8f5e9; border: 1px solid #c8e6c9; color: #2e7d32; padding: 4px 8px; border-radius: 4px; font-size: 0.75rem; font-weight: bold; cursor: pointer;'>
                            ✅ Confirmar
                        </button>
                        <button hx-post='/api/vendas/cancelar/{v.Id}' 
                                hx-include='#form-filtros'
                                hx-target='#painel-conteudo'
                                title='Descartar Pix não pago'
                                style='background: #f5f5f5; border: 1px solid #ddd; color: #666; padding: 4px 6px; border-radius: 4px; font-size: 0.75rem; cursor: pointer;'>
                            ✕
                        </button>
                    </div>
                ";
            } else
            {
                botoesAcao = $"<small style='color: {corStatus}; font-weight: bold;'>{v.Status}</small>";
            }

            sb.Append($@"
                <div style='display: flex; justify-content: space-between; align-items: center; background: #fafafa; border: 1px solid #eee; padding: 8px 10px; border-radius: 6px; font-size: 0.85rem;'>
                    <div>
                        <span style='font-weight: bold; color: #333;'>#{v.Id}</span>
                        <span style='color: #666; margin-left: 4px;'>{v.Quantidade}x</span>
                        <span style='margin-left: 4px;'>{badgeForma}</span>
                        <span style='font-weight: bold; margin-left: 6px; color: {corStatus};'>R$ {v.Valor:F2}</span>
                        <small style='color: #999; margin-left: 4px;'>{v.DataHora:dd/MM HH:mm}</small>
                    </div>
                    <div>
                        {botoesAcao}
                    </div>
                </div>
            ");
        }
    }

    sb.Append(@"
        </div>
            <div style='margin-top: 16px; text-align: center;'>
                <button hx-post='/api/auth/logout' style='background: #555; color: white; border: none; padding: 8px 16px; border-radius: 6px; font-size: 0.85rem; cursor: pointer;'>🚪 Sair (Logout)</button>
            </div>
        </div>
    ");

    return sb.ToString();
}

// ==========================================
// BANCO DE DADOS & MODELOS
// ==========================================
public class AppDbContext : DbContext, IDataProtectionKeyContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Venda> Vendas => Set<Venda>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
}

public class Venda
{
    public int Id { get; set; }
    public DateTime DataHora { get; set; }
    public int Quantidade { get; set; } = 1;

    [Precision(10, 2)]
    public decimal Desconto { get; set; } = 0.00m;

    [Precision(10, 2)]
    public decimal Valor { get; set; }

    public string FormaPagamento { get; set; } = "PIX";
    public string Status { get; set; } = string.Empty;
}

// ==========================================
// GERADOR PIX
// ==========================================
public static class GeradorPix
{
    public static string CriarPayload(string chave, string nome, string cidade, decimal valor, string txtId)
    {
        string valorFormatado = valor.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        string merchantAccount = FormatarTag("00", "BR.GOV.BCB.PIX") + FormatarTag("01", chave);
        string additionalData = FormatarTag("05", txtId);

        var payload = new StringBuilder();
        payload.Append(FormatarTag("00", "01"));
        payload.Append(FormatarTag("26", merchantAccount));
        payload.Append(FormatarTag("52", "0000"));
        payload.Append(FormatarTag("53", "986"));
        payload.Append(FormatarTag("54", valorFormatado));
        payload.Append(FormatarTag("58", "BR"));
        payload.Append(FormatarTag("59", nome.Length > 25 ? nome[..25] : nome));
        payload.Append(FormatarTag("60", cidade.Length > 15 ? cidade[..15] : cidade));
        payload.Append(FormatarTag("62", additionalData));
        payload.Append("6304");

        string crc = CalcularCRC16(payload.ToString());
        payload.Append(crc);

        return payload.ToString();
    }

    private static string FormatarTag(string id, string valor) => $"{id}{valor.Length:D2}{valor}";

    private static string CalcularCRC16(string str)
    {
        ushort crc = 0XFFFF;
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        foreach(byte b in bytes)
        {
            crc ^= (ushort)(b << 8);
            for(int i = 0; i < 8; ++i)
            {
                if((crc & 0x8000) != 0)
                {
                    crc = (ushort)((crc << 1) ^ 0x1021);
                } else
                {
                    crc <<= 1;
                }
            }
        }
        return crc.ToString("X4");
    }
}
