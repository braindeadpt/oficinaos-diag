# Migração WhatsApp — investigação técnica (Fase 0/1)

Investigação do motor de migração WhatsApp para o OficinaDiag (módulo Pro `wa-migrate`).
Base empírica: `ChatStorage.sqlite` real extraído de um iPhone 12 Pro (iOS 27.2.1)
com WhatsApp **Business** — 1 429 730 mensagens, modelo Core Data `2.12.14`.
Referência de mapeamento: código dos conversores open-source (leitura apenas —
GPL; a implementação no Diag é clean-room em C#, MIT).

## Onde encaixa no produto

As vias oficiais cobrem os casos "limpos" (Move to iOS, transferência in-app
iPhone→Android, cabo durante setup). O Diag cobre os buracos:

| Caso | Alternativa oficial |
|---|---|
| Android → iPhone **já configurado** | Nenhuma — Move to iOS exige reset |
| WhatsApp **Business**, ambos os sentidos | Nenhuma |
| iPhone → Android sem opção in-app + sem querer repor | Residual |
| **App corrupta, BD viva** (resgate) | Nenhuma — todas as vias oficiais exigem a app a funcionar |
| Export legível (PDF/HTML) — provas, memória | Nenhuma |

Gate Pro via catálogo de esquemas na cloud (`KNOWN_MODULES` + endpoint
`wa-schemas` — fingerprints de versão → tabelas de conversão; sem catálogo
não há conversão, e updates de esquema chegam sem release do Diag).

## ChatStorage.sqlite (iOS) — esquema observado

Core Data SQLite. 18 tabelas; as que interessam:

| Entidade | Z_ENT | Papel |
|---|---|---|
| `ZWACHATSESSION` | 4 | Conversas (1:1 e grupos): `ZCONTACTJID`, `ZSESSIONTYPE`, `ZUNREADCOUNT`, `ZMESSAGECOUNTER`, `ZLASTMESSAGE`, `ZLASTMESSAGEDATE`, `ZGROUPINFO`→link |
| `ZWAGROUPINFO` | 5 | Metadados do grupo: `ZCHATSESSION`, `ZCREATORJID`, `ZSUBJECTTIMESTAMP`, `ZPICTUREPATH` |
| `ZWAGROUPMEMBER` | 6 | Membros: `ZMEMBERJID`, `ZCHATSESSION`, `ZISADMIN`, `ZISACTIVE` |
| `ZWAMESSAGE` | ? | Mensagens: `ZSTANZAID`(=key_id), `ZISFROMME`, `ZMESSAGETYPE`, `ZTEXT`, `ZFROMJID`/`ZTOJID`, `ZMESSAGEDATE`, `ZMESSAGESTATUS`, `ZSORT`, `ZCHATSESSION`→, `ZMEDIAITEM`→, `ZPARENTMESSAGE`→(respostas), `ZPUSHNAME`, `ZSTARRED` |
| `ZWAMEDIAITEM` | ? | Media: `ZMEDIALOCALPATH`, `ZMEDIAURL`, `ZFILESIZE`, `ZMEDIAKEY`(blob — chave E2E do ficheiro), `ZMOVIEDURATION`, lat/long (localização) |
| `ZWAMESSAGEDATAITEM` | 10 | Payloads especiais (caption media, link preview) |
| `ZWAVCARDMENTION` | ? | vCards + mentions |
| `ZWAZ1PAYMENTTRANSACTION` | ? | Pagamentos WA Pay |

Mecânica Core Data obrigatória na escrita:
- `Z_PRIMARYKEY` mantém `Z_MAX` por `Z_ENT` — tem de ser incrementado
  consistentemente com os `Z_PK` inseridos ou o SQLite corrompe-se.
- `Z_METADATA` guarda o hash do modelo (`2.12.14` aqui) — o WhatsApp
  valida-o; escrever esquema de versão errada = app apaga/recria a BD.
- `ZSORT` ordena mensagens dentro da sessão; inserções em lote exigem
  shift dos sorts existentes (o conversor de referência faz
  `UPDATE ZWAMESSAGE SET ZSORT=?+ZSORT WHERE …`).
- `ZWACHATSESSION.ZMESSAGECOUNTER`/`ZLASTMESSAGE`/`ZLASTMESSAGEDATE` têm
  de ser recalculados no fim.

### Distribuição real de `ZMESSAGETYPE` (BD Business real, 1.43M msgs)

| type | msgs | ~significado |
|---|---|---|
| 0 | 1 266 140 | texto |
| 1 | 73 546 | imagem |
| 7 | 23 424 | vídeo |
| 15 | 15 512 | apagada (2) |
| 2 | 14 324 | áudio |
| 14 | 8 819 | contacto/vcard |
| 6 | 6 751 | evento de grupo |
| 3 | 5 745 | documento/app |
| 8 | 5 740 | localização |
| 10 | 4 154 | chamada |
| restantes | <1% cada | sticker, live-loc, payment, sistema… |

→ texto+imagem+vídeo+áudio cobrem ~97% do volume real.

## msgstore.db (Android) — esquema alvo

Tabelas relevantes (leitura do conversor de referência):
`chat` (conversas, `jid_row_id`→`jid`), `messages`, `jid`
(`raw_string` = JID completo), `legacy_available_messages_view`.

Campos `messages` usados na conversão:
`key_remote_jid` (JID do peer), `key_from_me`, `key_id` (=ZSTANZAID),
`data` (texto), `timestamp` (Unix **ms**), `media_wa_type`,
`remote_resource` (emissor no grupo), `media_url`, `media_name`,
`media_caption`, `media_size`, `thumb_image`.

### Mapa `media_wa_type` (Android) → `ZMESSAGETYPE` (iOS)

| Android | iOS obs. | tipo |
|---|---|---|
| 0 | 0 | texto |
| 1 | 1 | imagem |
| 3 | 7 | vídeo |
| 2 | 2 | áudio |
| 9 / 26 | 3 | documento/app |
| 4 | 14 | vcard/contacto |
| 5 / 16 | 8 | localização / live |
| 11 / 15 | 15 | apagada |
| 10 | 10 | chamada |
| 13 | ? | GIF |
| 20 | ? | sticker |

### Timestamps

iOS Core Data epoch = 2001-01-01 (`+978307200`s p/ Unix).
Android = Unix **ms**.

- iOS→Android: `timestamp = (ZMESSAGEDATE + 978307200) * 1000`
- Android→iOS: `ZMESSAGEDATE = timestamp/1000 - 978307200`
- Verificado em dados reais: `816652646` → `2026-11-17 23:57` ✓

## Extração iOS — o que correu no spike (iPhone 12 Pro, iOS 27.2.1)

- `idevicebackup2 backup --full` funciona (protocolo 2.1); device tem de
  estar **desbloqueado** (`MBErrorDomain/208` se bloqueado — todos os
  stalls observados eram isto).
- Backup completo de telefone real carregado: **~320k ficheiros /
  ~106 GB / ~4h**. Inaceitável como fluxo de loja — o produto tem de
  pedir só o domínio WhatsApp (ver "em aberto").
- Ficheiros chegam a buckets `<2 hex>/<sha1(domínio-path)>`.
  `ChatStorage` Business confirmado em
  `AppDomainGroup-group.net.whatsapp.WhatsAppSMB.shared/ChatStorage.sqlite`.
  Domínio do WhatsApp normal (por confirmar):
  `AppDomainGroup-group.net.whatsapp.WhatsApp.shared`.
- Matar o backup a 98% ("Moving"/finalize) deixou ficheiros no disco
  mas sem `Manifest.db` — lookups por hash funcionam mesmo assim.
- Media: paths em `ZWAMEDIAITEM.ZMEDIALOCALPATH`
  (`/Media/<chat>/xx/yy/<file>`) — **0/30 presentes** no backup
  parcial; ficou por verificar se faltou transferência ou se o
  domínio/path difere (verificar com `Manifest` quando o tivermos).

## Em aberto / próximos gates

- **Gate C (crítico)**: `idevicebackup2 restore` de um backup
  *domain-scoped* no iPhone configurado — escreve no container sem
  repor o resto? Requer Find My OFF + WhatsApp alvo (não-Business)
  instalado sem ativar.
- **Gate D**: WhatsApp abre o `ChatStorage` injetado sem o apagar?
  (versão do modelo tem de bater certo com a app instalada).
- Backup domain-scoped: o protocolo `mobilebackup2` aceita opções no
  pedido — testar se o device honra subset de domínios (a CLI não
  expõe; os bindings C# permitem pedido customizado).
- crypt15 (lado Android): decrypt com chave de 64 dígitos — AES-GCM,
  formato documentado; implementar clean-room.
- Media: confirmar domínio/path real e completude da transferência.
- Business vs normal: mesmas tabelas, pacote/domínio diferente —
  `com.whatsapp.w4b` / `net.whatsapp.WhatsAppSMB`.
- Matriz de devices: iPhone 12 Pro iOS 27.2.1 (Business) ✓ extração;
  iPhone XR iOS antigo + WhatsApp normal = 2º device de teste.
