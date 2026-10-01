# oficinaos-diag

Ferramenta Windows de diagnóstico de telemóveis por USB — visual terminal retro anos 90/2000,
scan local gratuito e relatórios Pro via [oficinaos-cloud] para lojas aderentes ao
[OficinaOS](https://github.com/braindeadpt/OficinaOS).

## O que faz

### Grátis — sempre, sem conta, sem servidor

- **Scan USB**: modelo, serial, versão do OS, bateria (nível, temperatura, ciclos,
  capacidade real vs design onde o fabricante expõe), storage, RAM, sensores,
  flags de root/bootloader (Android) ou ativação/operadora (iPhone)
- **Teste de ecrã + toque no telefone**: a app serve uma página de teste na rede
  local — Android abre-a sozinho via `adb`, iPhone lê um QR code. Resultados
  voltam ao PC: pixeis mortos e zonas de toque mortas ficam registados
- **Exportar**: relatório HTML retro (imprimível → PDF) + JSON bruto
- **Histórico local**: scans anteriores em `%APPDATA%\OficinaDiag` — comparar a
  saúde da bateria ao longo do tempo

### Pro — via oficinaos-cloud

- **Relatório IA** (`ai-reports`): os dados brutos do scan viram um relatório em
  linguagem simples para o cliente — gerado no servidor, nada corre localmente
- **Enviar à loja** (`intake`): o cliente em casa mete o código da loja e o
  diagnóstico chega à app OficinaOS da loja como pré-check — reparações,
  compra/venda de usados, grading

## Suporte de dispositivos

| Plataforma | Como | Requisitos no telefone | Drivers no PC |
|---|---|---|---|
| Android | `adb.exe` embutido | Depuração USB ativa | nenhum |
| iPhone/iPad | libimobiledevice (imobiledevice-net) | «Confiar neste computador» | driver USB da Apple (iTunes/Apple Devices) |

## Build

Requisitos: [.NET 8 SDK](https://dotnet.microsoft.com/download) e PowerShell.

```powershell
tools\fetch-tools.ps1          # descarrega platform-tools (adb) uma vez
dotnet publish src/OficinaDiag -r win-x64 --self-contained `
  -p:PublishSingleFile=true -o publish
```

O resultado é `publish\OficinaDiag.exe` + `tools\platform-tools\` ao lado —
portátil, basta copiar a pasta.

## Privacidade

O scan corre 100% local. Nada sai do PC sem o utilizador escolher «Enviar à
loja» ou «IA» — e mesmo aí o destino é o servidor OficinaOS, nunca terceiros
opacos (a IA corre com a chave do servidor, os dados não são treino).

## Licença

MIT — ver [LICENSE](LICENSE).
