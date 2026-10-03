# oficinaos-diag

Ferramenta Windows de diagnóstico de telemóveis por USB — visual terminal retro anos 90/2000,
scan local gratuito e relatórios Pro via [oficinaos-cloud] para lojas aderentes ao
[OficinaOS](https://github.com/braindeadpt/OficinaOS).

> **Ecossistema:** este é 1 de 4 repos (app da loja `reparilo`, `oficinaos-cloud`,
> `oficinaos-website`, esta ferramenta). O mapa canónico — fluxos de dados, IDs de
> módulos, onde mora cada peça — está em
> [`reparilo/docs/ecosystem.md`](https://github.com/braindeadpt/OficinaOS/blob/main/docs/ecosystem.md).

## Descarregar (testadores)

**[oficinaos-diag-win-x64.zip — última versão](https://github.com/braindeadpt/oficinaos-diag/releases/latest/download/oficinaos-diag-win-x64.zip)**

1. Descompacta o zip inteiro (não tires o exe da pasta — precisa dos ficheiros ao lado)
2. Corre `OficinaDiag.exe` — se o Windows avisar, vê [SmartScreen](#aviso-do-windows-smartscreen)
3. **Android**: ativa «Depuração USB» nas opções de programador do telefone e liga por cabo
4. **iPhone**: precisa do driver USB da Apple (iTunes ou app «Apple Devices» instalada); aceita «Confiar neste computador» no telefone. Se o Windows abrir a app Fotos para importar, fecha-a antes do scan

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

## Aviso do Windows (SmartScreen)

Na primeira execução o Windows mostra «o Windows protegeu o seu PC» — é normal:
o exe não tem assinatura de código, por isso o SmartScreen não tem reputação
dele. Carrega **«Mais informações» → «Executar mesmo assim»** (só pergunta uma
vez por ficheiro). Não é malware — é só um programa caseiro sem certificado.

Para distribuir a lojas sem o aviso é preciso assinar o exe (code-signing cert
~€100–500/ano ou Azure Trusted Signing ~$10/mês) — fazer quando o Pro sair.

## Log de depuração

Tudo o que aparece na consola da app + erros fica em
`%APPDATA%\OficinaDiag\diag.log`. Se algo falhar, manda esse ficheiro — ou usa o
botão **[ ENVIAR LOG ]** na app, que envia a cauda do log ao nosso servidor
(`cloud.oficinaos.app`) para análise. É sempre opt-in: nada é enviado sem o
clique, e o log contém apenas linhas de diagnóstico da app (sem fotos, contactos
ou dados do telefone além do que o scan já mostra).

## Privacidade

O scan corre 100% local. Nada sai do PC sem o utilizador escolher «Enviar à
loja» ou «IA» — e mesmo aí o destino é o servidor OficinaOS, nunca terceiros
opacos (a IA corre com a chave do servidor, os dados não são treino).

## Licença

MIT — ver [LICENSE](LICENSE).
