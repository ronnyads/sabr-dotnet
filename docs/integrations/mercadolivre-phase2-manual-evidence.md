# Mercado Livre Fase 2 - Evidencias manuais (curl)

## 1) Categories/attributes com allowedAxes

```bash
curl -X POST "http://localhost:5250/api/v1/client/marketplaces/categories/attributes" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <CLIENT_JWT>" \
  -d '{"integrationId":"<GUID>","sellerId":"<SELLER_ID_OPCIONAL>","categoryId":"MLB1055"}'
```

Resposta esperada (exemplo):

```json
{
  "allowsVariations": true,
  "maxVariationsAllowed": 10,
  "maxVariationAttributes": 2,
  "allowedVariationAttributes": ["COLOR", "SIZE"],
  "allowedAxes": ["Cor", "Tamanho"],
  "requiredAttributes": [],
  "conditionalAttributes": []
}
```

## 2) Draft validate invalido com fieldPath

```bash
curl -X POST "http://localhost:5250/api/v1/client/listings/drafts/validate" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <CLIENT_JWT>" \
  -d '{"draftId":"<DRAFT_GUID>"}'
```

Resposta esperada quando eixo invalido (exemplo):

```json
{
  "draftId": "00000000-0000-0000-0000-000000000000",
  "isValid": false,
  "issues": [
    {
      "fieldPath": "variationAxes[0]",
      "code": "VARIATION_AXIS_NOT_ALLOWED",
      "message": "Variation axis 'Material' is not allowed. Allowed axes: Cor, Tamanho.",
      "severity": "error",
      "step": "attributes"
    }
  ]
}
```
