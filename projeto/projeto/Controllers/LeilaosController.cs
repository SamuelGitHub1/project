﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using projeto.Data;
using projeto.Models;

namespace projeto.Controllers
{
    public class LeilaosController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;

        public LeilaosController(ApplicationDbContext context, IWebHostEnvironment env)
        {
            _context = context;
            _env = env;
        }

        // GET: Leilaos
        public async Task<IActionResult> Index(string categorias, string tempo, double? min, double? max)
        {
            var userEmail = HttpContext.Session.GetString("UserEmail");
            var user = await _context.Utilizador.FirstOrDefaultAsync(u => u.Email == userEmail);

            ViewData["UserPoints"] = user?.Pontos;
            ViewData["Logged"] = user != null;
            ViewData["UserId"] = user?.UtilizadorId;

            var categoriasEnum = Enum.GetValues(typeof(Categoria)).Cast<Categoria>().ToList();
            ViewData["Categorias"] = categoriasEnum;

            var query = _context.Leiloes.Include(l => l.Item).AsQueryable();

            // Aplicar filtros
            if (!string.IsNullOrEmpty(categorias))
            {
                var categoriasSelecionadas = categorias.Split(',').Select(c => Enum.Parse<Categoria>(c)).ToList();
                query = query.Where(l => categoriasSelecionadas.Contains(l.Item.Categoria));
            }

            if (!string.IsNullOrEmpty(tempo))
            {
                var agora = DateTime.Now;
                var filtrosTempo = tempo.Split(',');

                if (filtrosTempo.Contains("< 1d")) query = query.Where(l => l.DataFim <= agora.AddDays(1));
                else if (filtrosTempo.Contains("< 5d")) query = query.Where(l => l.DataFim <= agora.AddDays(5));
                else if (filtrosTempo.Contains("< 10d")) query = query.Where(l => l.DataFim <= agora.AddDays(10));
            }

            // Aplicar filtros de preço
            if (min.HasValue)
                query = query.Where(l => l.ValorAtualLance >= min.Value);  // Substituir PrecoInicial por ValorAtualLance
            if (max.HasValue)
                query = query.Where(l => l.ValorAtualLance <= max.Value);  // Substituir PrecoInicial por ValorAtualLance


            var leiloes = await query.ToListAsync();

            // Calcular o maior lance para cada leilão
            var leilaoIds = leiloes.Select(l => l.LeilaoId).ToList();
            var maioresLances = await _context.Licitacoes
                .Where(l => leilaoIds.Contains(l.LeilaoId))
                .GroupBy(l => l.LeilaoId)
                .Select(g => new { LeilaoId = g.Key, MaiorLance = g.Max(l => (double?)l.ValorLicitacao) })
                .ToListAsync();

            foreach (var leilao in leiloes)
            {
                leilao.ValorAtualLance = maioresLances.FirstOrDefault(l => l.LeilaoId == leilao.LeilaoId)?.MaiorLance ?? leilao.Item.PrecoInicial;
            }

            // Verificar e encerrar leilões
            foreach (var leilao in leiloes.Where(l => DateTime.Now > l.DataFim && l.EstadoLeilao != EstadoLeilao.Encerrado))
            {
                leilao.EstadoLeilao = EstadoLeilao.Encerrado;
                var licitacaoVencedora = await _context.Licitacoes
                    .Where(l => l.LeilaoId == leilao.LeilaoId)
                    .OrderByDescending(l => l.ValorLicitacao)
                    .FirstOrDefaultAsync();

                if (licitacaoVencedora != null)
                {
                    var vencedor = await _context.Utilizador.FindAsync(licitacaoVencedora.UtilizadorId);
                    if (vencedor != null)
                    {
                        vencedor.Pontos += leilao.Item.Sustentavel ? 50 : 20;
                        _context.Update(vencedor);
                        leilao.Vencedor = vencedor.Nome;
                    }
                }
                _context.Update(leilao);
            }

            await _context.SaveChangesAsync();
            return View(leiloes);
        }



        // GET: Leilaos/Details/5
        public async Task<IActionResult> Details(int id)
        {
            var userEmail = HttpContext.Session.GetString("UserEmail");
            var user = await _context.Utilizador.FirstOrDefaultAsync(u => u.Email == userEmail);
            ViewData["UserPoints"] = user?.Pontos;

            var leilao = await _context.Leiloes
                .Include(l => l.Item)
                .FirstOrDefaultAsync(l => l.LeilaoId == id);

            if (leilao == null)
            {
                return NotFound();
            }

            if (leilao.EstadoLeilao == EstadoLeilao.Encerrado)
            {
                leilao.Licitacoes = await _context.Licitacoes
                    .Where(l => l.LeilaoId == id)
                    .OrderByDescending(l => l.ValorLicitacao) 
                    .ToListAsync();
            }

            return View(leilao);  
        }

        // GET: Leilaos/Create
        public async Task<IActionResult> Create()
        {
            ViewData["Categorias"] = Enum.GetValues(typeof(Categoria))
                                 .Cast<Categoria>()
                                 .Select(c => new SelectListItem
                                 {
                                     Value = c.ToString(),
                                     Text = c.ToString()
                                 }).ToList();

            var userEmail = HttpContext.Session.GetString("UserEmail");

            var user = await _context.Utilizador.FirstOrDefaultAsync(u => u.Email == userEmail);

            ViewData["UserPoints"] = user?.Pontos;

            if (user == null) {
                return RedirectToAction("Login", "Utilizadors"); 

            }

            return View();
        }

        // POST: Leilaos/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Leilao leilao)
        {
            var userEmail = HttpContext.Session.GetString("UserEmail");
            var user = await _context.Utilizador.FirstOrDefaultAsync(u => u.Email == userEmail);

            if (user == null)
            {
                return RedirectToAction("Login", "Utilizadors"); 
            }

            if (leilao.Item.fotoo == null || leilao.Item.fotoo.Length == 0)
            {
                // Se não, adiciona o erro de validação para a foto
                ModelState.AddModelError("Item.fotoo", "A foto é obrigatória.");
                return View(leilao); // Retorna a view com o erro
            }

            leilao.UtilizadorId = user.UtilizadorId;

            if (leilao.Item.fotoo != null && leilao.Item.fotoo.Length > 0)
            {
                string folder = "leilao/fotos/";
                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(leilao.Item.fotoo.FileName);  
                string serverFolder = Path.Combine(_env.WebRootPath, folder);

                if (!Directory.Exists(serverFolder))
                {
                    Directory.CreateDirectory(serverFolder);
                }

                string filePath = Path.Combine(serverFolder, fileName);

                if (leilao.Item.fotoo.Length > 5 * 1024 * 1024)  
                {
                    ModelState.AddModelError("Item.fotoo", "Size is to big");
                    return View(leilao); 
                }

                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif" };
                if (!allowedExtensions.Contains(Path.GetExtension(leilao.Item.fotoo.FileName).ToLower()))
                {
                    ModelState.AddModelError("Item.fotoo", "Only extensions (.jpg, .jpeg, .png, .gif) allowed.");
                    return View(leilao); 
                }

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await leilao.Item.fotoo.CopyToAsync(stream);
                }

                leilao.Item.FotoUrl = "/" + folder + fileName;
            }

            _context.Add(leilao);
            await _context.SaveChangesAsync(); 

            return RedirectToAction(nameof(Index));  
        }

        // GET: Leilaos/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var leilao = await _context.Leiloes.FindAsync(id);
            if (leilao == null)
            {
                return NotFound();
            }
            ViewData["ItemId"] = new SelectList(_context.Itens, "ItemId", "ItemId", leilao.ItemId);
            return View(leilao);
        }

        // POST: Leilaos/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("LeilaoId,ItemId,DataInicio,DataFim,ValorIncrementoMinimo,Vencedor")] Leilao leilao)
        {
            if (id != leilao.LeilaoId)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(leilao);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!LeilaoExists(leilao.LeilaoId))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            ViewData["ItemId"] = new SelectList(_context.Itens, "ItemId", "ItemId", leilao.ItemId);
            return View(leilao);
        }

        // GET: Leilaos/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var leilao = await _context.Leiloes
                .Include(l => l.Item)
                .FirstOrDefaultAsync(m => m.LeilaoId == id);
            if (leilao == null)
            {
                return NotFound();
            }

            return View(leilao);
        }

        // POST: Leilaos/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var leilao = await _context.Leiloes
                .Include(l => l.Item) 
                .FirstOrDefaultAsync(l => l.LeilaoId == id);

            if (leilao != null)
            {
                if (leilao.Item != null)
                {
                    _context.Itens.Remove(leilao.Item);
                }
                _context.Leiloes.Remove(leilao);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Index));
        }


        private bool LeilaoExists(int id)
        {
            return _context.Leiloes.Any(e => e.LeilaoId == id);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> FazerLicitacao(int leilaoId, double valorLicitacao)
        {
            var userEmail = HttpContext.Session.GetString("UserEmail");
            var user = await _context.Utilizador.FirstOrDefaultAsync(u => u.Email == userEmail);

            if (user == null)
            {
                return RedirectToAction("Login", "Utilizadors");
            }

            var leilao = await _context.Leiloes
                .Include(l => l.Licitacoes)
                .Include(l => l.Item) 
                .FirstOrDefaultAsync(l => l.LeilaoId == leilaoId);

            if (leilao == null)
            {
                return NotFound();
            }

            if (leilao.EstadoLeilao == EstadoLeilao.Encerrado || DateTime.Now > leilao.DataFim)
            {
                TempData["Error"] = "This auction has already ended and no longer accepts bids..";
                return RedirectToAction("Index", "Leilaos"); 
            }

            double lanceMinimo = leilao.Licitacoes.Any()
                ? leilao.Licitacoes.Max(l => l.ValorLicitacao)
                : leilao.Item.PrecoInicial;

            double valorNecessario = lanceMinimo + leilao.ValorIncrementoMinimo;

            if (valorLicitacao < valorNecessario)
            {
                TempData["Error"] = $"The bid must be higher than {valorNecessario:C2}.";
                return RedirectToAction("Index", "Leilaos"); 
            }

            var licitacao = new Licitacao
            {
                LeilaoId = leilaoId,
                UtilizadorId = user.UtilizadorId,
                ValorLicitacao = valorLicitacao,
                DataLicitacao = DateTime.Now
            };

            _context.Licitacoes.Add(licitacao);
            user.Pontos += 1;
            _context.Update(user);

            await _context.SaveChangesAsync();

            TempData["Success"] = "Sucessfull bid!";
            return RedirectToAction("Index", "Leilaos"); 
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> FazerLicitacaoDetails(int leilaoId, double valorLicitacao)
        {
            var userEmail = HttpContext.Session.GetString("UserEmail");
            var user = await _context.Utilizador.FirstOrDefaultAsync(u => u.Email == userEmail);

            if (user == null)
            {
                return RedirectToAction("Login", "Utilizadors");
            }

            var leilao = await _context.Leiloes
                .Include(l => l.Licitacoes)
                .Include(l => l.Item)
                .FirstOrDefaultAsync(l => l.LeilaoId == leilaoId);

            if (leilao == null)
            {
                return NotFound();
            }

            if (leilao.UtilizadorId == user.UtilizadorId)
            {
                TempData["BidError"] = "You cannot place bids on your own auction.";
                return RedirectToAction("Details", new { id = leilaoId });
            }

            if (leilao.EstadoLeilao == EstadoLeilao.Encerrado || DateTime.Now > leilao.DataFim)
            {
                TempData["BidError"] = "This auction has already ended and no longer accepts bids.";
                return RedirectToAction("Details", new { id = leilaoId });
            }

            double lanceMinimo = leilao.Licitacoes.Any()
                ? leilao.Licitacoes.Max(l => l.ValorLicitacao)
                : leilao.Item.PrecoInicial;

            double valorNecessario = lanceMinimo + leilao.ValorIncrementoMinimo;

            if (valorLicitacao < valorNecessario)
            {
                TempData["BidError"] = $"The bid must be higher than {valorNecessario:C2}.";
                return RedirectToAction("Details", new { id = leilaoId });
            }

            var licitacao = new Licitacao
            {
                LeilaoId = leilaoId,
                UtilizadorId = user.UtilizadorId,
                ValorLicitacao = valorLicitacao,
                DataLicitacao = DateTime.Now
            };

            _context.Licitacoes.Add(licitacao);
            user.Pontos += 1;
            _context.Update(user);
            await _context.SaveChangesAsync();

            leilao = await _context.Leiloes
                .Include(l => l.Licitacoes)
                .Include(l => l.Item)
                .FirstOrDefaultAsync(l => l.LeilaoId == leilaoId);

            if (leilao != null)
            {
                leilao.ValorAtualLance = leilao.Licitacoes.Max(l => l.ValorLicitacao);
                _context.Update(leilao);
                await _context.SaveChangesAsync();
            }

            TempData["Success"] = "Successful bid!";
            return RedirectToAction("Details", new { id = leilaoId });
        }

        public async Task<IActionResult> AtualizarEstadoLeiloes()
        {
            var leiloes = await _context.Leiloes.ToListAsync();

            foreach (var leilao in leiloes)
            {
                if (DateTime.Now > leilao.DataFim)
                {
                    if (leilao.EstadoLeilao == EstadoLeilao.Disponivel)
                    {
                        leilao.EstadoLeilao = EstadoLeilao.Encerrado;

                        var vencedor = leilao.Licitacoes.OrderByDescending(l => l.ValorLicitacao).FirstOrDefault();
                        if (vencedor != null)
                        {
                            // Aqui você pode, por exemplo, atualizar o campo VencedorId ou fazer outra lógica
                            // Para o momento, vamos manter a informação apenas no leilão.
                        }
                    }
                }

                _context.Update(leilao);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction("Index");
        }

        public async Task<IActionResult> MyAuctions(int page = 1)
        {
            int pageSize = 3;
            var userEmail = HttpContext.Session.GetString("UserEmail");
            var user = await _context.Utilizador.FirstOrDefaultAsync(u => u.Email == userEmail);

            if (user == null)
            {
                return RedirectToAction("Login", "Utilizadors");
            }

            var totalLeiloes = await _context.Leiloes.CountAsync(l => l.UtilizadorId == user.UtilizadorId);
            var leiloes = await _context.Leiloes
                .Where(l => l.UtilizadorId == user.UtilizadorId)
                .Include(l => l.Item)
                .OrderBy(l => l.DataFim)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = (int)Math.Ceiling((double)totalLeiloes / pageSize);

            return View(leiloes);
        }

        public async Task<IActionResult> MyBids()
        {
            var userEmail = HttpContext.Session.GetString("UserEmail");
            var user = await _context.Utilizador.FirstOrDefaultAsync(u => u.Email == userEmail);

            if (user == null)
            {
                return RedirectToAction("Login", "Utilizadors");
            }

            var meusLances = await _context.Licitacoes
                .Where(l => l.UtilizadorId == user.UtilizadorId)
                .Include(l => l.Leilao)
                .ThenInclude(l => l.Item)
                .OrderByDescending(l => l.DataLicitacao)
                .ToListAsync();

            return View(meusLances);
        }
    }
}
