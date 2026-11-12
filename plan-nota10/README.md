# Guia Mestre de Execução Multi-Agente — `oficinaos-diag` rumo a Nota 10

Este diretório (`plan-nota10/`) contém **todo o contexto da auditoria técnica, especificações de engenharia e tickets auto-contidos** para elevar o repositório `braindeadpt/oficinaos-diag` (`v0.2.1`) de **7.4/10 para 10/10**.

Foi desenhado especificamente para **portabilidade total entre agentes e contas LLM** (Traycer, Claude Code, OpenAI Codex, Cursor, OpenCode, Gemini CLI, Windsurf, Aider). Nenhum agente precisa do histórico de conversa original: tudo o que é preciso está aqui.

---

## Como retomar o trabalho em qualquer Agente / Conta LLM

Copia e cola este prompt em qualquer ferramenta ou agente de código apontado para a raiz deste repositório:

```text
És o engenheiro responsável por implementar o plano em `plan-nota10/`.
1. Lê `plan-nota10/STATUS.md` para veres quais os tickets já concluídos e qual é o próximo pendente.
2. Lê apenas o ficheiro do ticket alvo em `plan-nota10/tickets/T-XX-*.md` e os ficheiros de código nele listados (não desperdices tokens a explorar o resto do repositório).
3. Antes de editar, confirma cada âncora `ficheiro:linha` do ticket com `rg -n '<símbolo>' <ficheiro>`. Se o ficheiro já mudou, localiza o bloco pela descrição do ticket e corrige o número no próprio ticket.
4. Implementa o ticket respeitando rigorosamente o estilo do projeto (.NET 8, C# 12, comentários e commits concisos em português europeu).
5. Corre os testes aplicáveis (`dotnet test tests/OficinaDiag.Tests`) se o .NET 8 SDK estiver instalado na máquina.
6. Atualiza `plan-nota10/STATUS.md` marcando o ticket e os respetivos bugs `B-xx` como `[x]` e regista notas curtas de handoff para o próximo agente.
```

> **Baseline verificado (2026-11-12):** todas as âncoras `ficheiro:linha` foram confirmadas contra o commit `3214f7a` (2026-10-16). Se o `HEAD` já avançou, o número é uma pista — o ticket descreve sempre o bloco, por isso localiza-o pelo símbolo antes de editar.

---

## Regras de Ouro do Repositório (Contexto para Agentes)

1. **Stack & Compatibilidade Multi-OS dos Testes:**
   - Aplicação desktop: `src/OficinaDiag/OficinaDiag.csproj` (`net8.0-windows`, WPF, `PublishSingleFile=true`).
   - Testes unitários: `tests/OficinaDiag.Tests/OficinaDiag.Tests.csproj` (`net8.0` puro — **tem de compilar e correr em Linux, macOS e Windows** porque o CI GitHub Actions corre em `ubuntu-latest`). Nunca introduzas `System.Windows` em código partilhado com `OficinaDiag.Tests` / `OficinaDiag.Core`.
   - Em macOS/Linux sem Windows Desktop SDK, `dotnet test tests/OficinaDiag.Tests` compila e corre todos os testes; apenas `src/OficinaDiag` (WPF) exige Windows ou `EnableWindowsTargeting=true` para compilar.
2. **Estilo e Convenções do Código:**
   - C# 12 com `Nullable=enable`, `ImplicitUsings=enable`, namespaces file-scoped (`namespace OficinaDiag...;`).
   - Comentários explicativos do *porquê* em **Português Europeu** (`pt-PT`), diretos e sem jargão desnecessário.
   - Commits seguem Conventional Commits em português (ex.: `fix(ios): preserva chaves de topo em ParsePlistDict e unifica battery.capacity`).
   - As versões das GitHub Actions em `.github/workflows/*.yml` (`actions/checkout@v7`, `actions/setup-dotnet@v6`, `softprops/action-gh-release@v3`) são **intencionais** (Node 24 runtime, PR #9) — **NÃO reverter** para v4/v2.
   - O diretório `plan-nota10/` não começa por ponto **de propósito**: `rg`, `glob` e os ficheiros de contexto dos agentes têm de o encontrar sozinhos. Não o renomeies para `.plan-nota10/`.
3. **Limpeza antes de Release Oficial:**
   - A pasta `plan-nota10/` serve de caderno de encargos local. Adiciona `plan-nota10/` ao `.gitignore` se não quiseres versioná-la no GitHub público, ou apaga-a após concluir `T-10`.

---

## Mapa das 4 Fases, Ordem de Execução e Paralelismo

Para poupar tokens e tempo, vários tickets tocam em ficheiros disjuntos e podem correr em paralelo por subagentes económicos (ex.: Gemini Flash / Argon, Haiku, GPT-mini):

| Fase | Ticket | Ficheiro do Ticket | Bugs Fechados | Depende de | Pode correr em paralelo com | Modelo / Perfil Sugerido |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Fase 1** | **T-01** | `tickets/T-01-updater-msix-guard.md` | `B-01`, `B-16` | — | `T-02`, `T-03`, `T-05` | Médio (Argon / Sonnet) |
| **Fase 1** | **T-02** | `tickets/T-02-ios-collector-grading-parity.md` | `B-02`, `B-03`, `B-04`, `B-09` | — | `T-01`, `T-05` | Forte/Médio (Argon) |
| **Fase 1** | **T-03** | `tickets/T-03-android-locale-polarity.md` | `B-05`, `B-06`, `B-11`, `B-19`, `B-20` | `T-02` *(partilha `IosCollector.cs` nos `TryParse`)* | `T-01`, `T-04`, `T-05` | Rápido (Flash) |
| **Fase 2** | **T-04** | `tickets/T-04-reports-cloud-hardening.md` | `B-08`, `B-12c`, `B-17`, `B-18` | — | `T-01`, `T-02`, `T-03`, `T-05` | Rápido (Flash) |
| **Fase 2** | **T-05** | `tickets/T-05-testserver-lan-safari.md` | `B-10`, `B-13` | — | `T-01`, `T-02`, `T-03`, `T-04` | Rápido (Flash) |
| **Fase 2** | **T-06** | `tickets/T-06-wpf-themes-l10n.md` | `B-07`, `B-12`, `B-14`, `B-15` | `T-01`, `T-04` *(partilha `MainWindow.xaml.cs` e `CloudClient.cs`)* | `T-03`, `T-05` | Rápido (Flash) |
| **Fase 3** | **T-07** | `tickets/T-07-extract-oficinadiag-core.md` | Dívida arquitetural `Linked/*.cs` | `T-01`..`T-06` | — *(barreira estrutural)* | Forte/Médio (Argon) |
| **Fase 3** | **T-08** | `tickets/T-08-regression-tests-ci.md` | Cobertura de todos os 20 bugs + CI | `T-07` | — | Médio (Argon / Flash) |
| **Fase 4** | **T-09** | `tickets/T-09-multi-device-oem-health.md` | Diferenciador Bancada #1 e #2 | `T-07`, `T-08` | `T-10` *(exceto `MainWindow` se simultâneo — preferir sequencial)* | Forte/Médio (Argon) |
| **Fase 4** | **T-10** | `tickets/T-10-test-v3-adb-reverse-labels-gdpr.md` | Diferenciador Bancada #3 e #4 | `T-07`, `T-08` (`T-09` se tocar `MainWindow`) | — | Médio (Argon / Flash) |
