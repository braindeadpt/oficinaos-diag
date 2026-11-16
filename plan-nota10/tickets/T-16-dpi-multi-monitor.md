# T-16 — A janela a 125/150% e em dois monitores

- **Fase:** 8 (o que faltava para a UI poder ser dita «nota 10» fora de 100%)
- **Bugs Resolvidos:** nenhum `B-xx` — é o defeito que a fase 8 declarou por verificar
- **Dependências:** `T-11` (capturas e harness), janela real num Windows
- **Ficheiros em Escopo:**
  - `src/OficinaDiag.Core/Ui/WindowFit.cs` (novo — geometria pura)
  - `src/OficinaDiag/MainWindow.xaml.cs` (`AjustarAoEcra`, `DpiChanged`, mudança de monitor)
  - `tools/UiShots/Program.cs` (secção `dpi:`)
  - `tests/OficinaDiag.Tests/WindowFitTests.cs`
  - `tools/mutation-check.mjs`

## Contexto

O `OnLoaded` fazia `MaxWidth = SystemParameters.WorkArea.Width` e centrava a janela ali. Duas
coisas erradas nisso, e as duas só se vêem fora de 100%:

1. **`SystemParameters.WorkArea` é a do monitor principal.** Num portátil com dock — o caso de
   uma bancada — a janela pode nascer no ecrã pequeno e ser limitada pela área do grande (ou o
   contrário, que é pior: nasce maior do que o ecrã onde está).
2. **Nada reagia a `DpiChanged`.** Mudar a escala com a app aberta, ou arrastá-la para um monitor
   de outro escalão, deixava o `MaxWidth`/`MaxHeight` presos à escala do arranque.

Faltava também a conta em DIP: a 150%, 1920 px são **1280 DIP**, e é isso que faz uma janela de
960 DIP deixar de caber num 1366×768.

## Decisões que ficam registadas

1. **A geometria é uma função pura** (`WindowFit.Calcular` + `WindowFit.ParaDip`), na Core, sem
   WPF nem P/Invoke. O `MainWindow` só aplica. É o que torna 100/125/150% testável em qualquer
   sistema, e é o que permite injectar a área no harness (`WindowFit.AreaForcada`, nula em
   produção) sem mudar a escala do Windows.
2. **O único caminho que aplica a área é `AjustarAoEcra`.** Arranque, `DpiChanged` e mudança de
   monitor passam todos por lá; um `_aAjustar` impede o ciclo `LocationChanged` → ajustar →
   `LocationChanged`.
3. **Dois ecrãs com a mesma escala não levantam `DpiChanged`.** Por isso o `LocationChanged`
   compara o handle do monitor e só reajusta quando ele muda.
4. **Sem área fiável não se corta nada.** P/Invoke falhado, `NaN` ou área zero devolvem o tamanho
   pedido e `MaxWidth = +∞` — perder a moldura por um erro de medição é pior do que não a limitar.
5. **`MinWidth`/`MinHeight` continuam a mandar abaixo do encolhimento.** A 150% num 1366×768 a
   área útil é 910×512 DIP, ainda acima dos 700×480 mínimos — por isso o caso cabe.

## Critérios

- [x] A geometria da janela vive numa função pura, testada a 100/125/150% e num portátil 1366×768 a 150%, sem WPF e sem PInvoke. *(prova: `WindowFitTests` — os três escalões, o centro por escalão, o portátil pequeno, a janela pequena que não é esticada, o segundo monitor à direita, os quatro casos de área inválida e a conversão px→DIP; `dotnet test` a 343 verdes)*
- [x] A janela aplica a área útil do monitor onde está e volta a caber quando o escalão ou o monitor mudam — com prova no harness a 100/125/150%. *(prova: secção `dpi:` do `tools/UiShots`, que injecta a área em DIP e passa pelo mesmo `AjustarAoEcra` do arranque e do `DpiChanged`: «a {pct}% cabe na área útil», «a {pct}% fica dentro do ecrã», «a 150% num 1366×768 encolhe», «o máximo acompanha a área do monitor» e «sem área injectada volta ao monitor real»)*
- [x] Um mutante que ignora a área útil (largura pedida sem `Math.Min`) é morto pelos testes, para a prova não passar a verde se a conta desaparecer. *(prova: entrada «a janela ignora a área útil» do `tools/mutation-check.mjs`, guarda `testes`, filtro `WindowFitTests`, a citar `ACaixaCabeSempreNaAreaUtil`)*

## Limitações

- O harness injeta a área; **não muda a escala do sistema**. O que fica provado é o caminho que
  aplica a geometria e a conta px→DIP; ver a janela bonita a 150% num monitor real continua a ser
  trabalho do teste de hardware (`plan-nota10/PROMPT-T-10-hardware.md`, Parte B).
- A barra de tarefas entra na `rcWork` do Windows, por isso a área útil real é menor do que a que
  o harness injecta; as asserções usam margem de 0,5 DIP e não dependem dessa diferença.
