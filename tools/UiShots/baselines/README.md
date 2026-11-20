# Baselines visuais do UiShots

Um PNG por captura do harness (`tools/UiShots`), com o mesmo nome do ficheiro
que o run grava em `out-store/ui-shots` (`main-terminal.png`, `combo-win95-aberto.png`, …).

No CI, o harness corre com `--baselines tools/UiShots/baselines` e reprova uma
captura quando mais de 0,5% dos pixéis se afastam do baseline (canais com
diferença > 16, para o anti-aliasing não contar). A diferença fica em
`diff-<nome>.png` no artefacto do run, com os pixéis diferentes a vermelho.

## Atualizar os baselines

Quando uma mudança de UI é intencional, corre num Windows:

```powershell
tools/atualizar-baselines-ui.ps1
```

O script corre o harness para `out-store/ui-shots` e copia os PNGs para aqui.
Reveja o diff das imagens no PR como se revê código.
