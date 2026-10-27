# GitHub Actions → Azure App Service con OIDC (sin secretos)

Repositorio de referencia que despliega una API ASP.NET Core (.NET 10) en Azure App Service desde GitHub Actions usando **Workload Identity Federation** (OIDC). El workflow no guarda ningún secreto: GitHub emite un token OIDC de corta duración y Microsoft Entra ID lo intercambia por un token de acceso de Azure.

La aplicación de ejemplo es una copia de [mbarretot/dotnet-clean-architecture](https://github.com/mbarretot/dotnet-clean-architecture). Solo se despliega el proyecto `src/CleanArchitecture.Presentation`.

## Qué demuestra

- El paso de un login con **client secret** ("Antes") a un login **OIDC** ("Después").
- Un service principal con el rol **Website Contributor** limitado a una sola Web App, no a la suscripción ni al grupo de recursos.
- El formato de **subject inmutable** que GitHub usa en los repositorios nuevos y cómo leerlo del log.
- Cómo `environment:` cambia el subject del token y por qué necesita su propia credencial federada.
- Los errores más comunes (`id-token: write`, `AADSTS700213`, `AADSTS700212`), con su causa y su solución.

## Antes y después

| | Antes (client secret) | Después (OIDC) |
|---|---|---|
| Workflow | [`docs/workflows-antes/deploy-client-secret.yml`](docs/workflows-antes/deploy-client-secret.yml) (solo referencia, no se ejecuta) | [`.github/workflows/deploy.yml`](.github/workflows/deploy.yml) |
| Secretos en GitHub | `AZURE_CLIENT_SECRET` + tres IDs | Solo tres IDs (no son sensibles; se guardan como secretos para que aparezcan enmascarados en los logs) |
| Credencial en Entra ID | Client secret con fecha de caducidad | Credencial federada: confía en un emisor, un subject y una audiencia concretos |
| Rotación | Manual, antes de que caduque | No hay nada que rotar: cada token dura 5 minutos |

El cambio en el workflow se reduce a dos piezas:

```yaml
permissions:
  id-token: write   # permite al job pedir un token OIDC a GitHub
  contents: read

# ...

      - name: Azure login (OIDC)
        uses: azure/login@v3
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
```

En lugar de:

```yaml
      - name: Azure login (client secret)
        uses: azure/login@v3
        with:
          creds: >-
            {"clientId":"${{ secrets.AZURE_CLIENT_ID }}",
             "clientSecret":"${{ secrets.AZURE_CLIENT_SECRET }}",
             "subscriptionId":"${{ secrets.AZURE_SUBSCRIPTION_ID }}",
             "tenantId":"${{ secrets.AZURE_TENANT_ID }}"}
```

## Cómo reproducirlo

Requisitos: Azure CLI con sesión iniciada, GitHub CLI (`gh`) y un fork o copia de este repositorio.

### 1. Infraestructura

```bash
SUB="<id-de-tu-suscripcion>"
RG="rg-github-oidc-demo"
LOCATION="centralus"
PLAN="asp-github-oidc-demo"
APP="github-oidc-demo-$RANDOM"          # el nombre debe ser único en *.azurewebsites.net
REPO="<owner>/<repo>"

az account set --subscription "$SUB"
az group create -n "$RG" -l "$LOCATION"
az appservice plan create -n "$PLAN" -g "$RG" -l "$LOCATION" --is-linux --sku F1
az webapp create -n "$APP" -g "$RG" -p "$PLAN" --runtime "DOTNETCORE:10.0"

# La app aplica migraciones de PostgreSQL al arrancar. Sin base de datos, se desactivan:
az webapp config appsettings set -n "$APP" -g "$RG" \
  --settings Database__ApplyMigrationsOnStartup=false
```

El plan F1 (gratuito) no está disponible en todas las regiones ni para todas las suscripciones. Si `az appservice plan create` responde `Operation cannot be completed without additional quota`, prueba otra región.

Después, cambia `AZURE_WEBAPP_NAME` en `.github/workflows/deploy.yml` por el valor de `$APP`.

### 2. App registration, service principal y rol

```bash
APP_ID=$(az ad app create --display-name github-actions-azure-oidc \
  --sign-in-audience AzureADMyOrg --query appId -o tsv)
SP_ID=$(az ad sp create --id "$APP_ID" --query id -o tsv)
WEBAPP_ID=$(az webapp show -n "$APP" -g "$RG" --query id -o tsv)

az role assignment create \
  --assignee-object-id "$SP_ID" --assignee-principal-type ServicePrincipal \
  --role "Website Contributor" --scope "$WEBAPP_ID"
```

`Website Contributor` con alcance en la Web App basta para que `azure/webapps-deploy` publique el paquete. El pipeline no puede crear ni borrar otros recursos.

### 3. Credencial federada

Primero consulta el prefijo real del subject de tu repositorio:

```bash
gh api "repos/$REPO/actions/oidc/customization/sub"
# {"use_default":true,"use_immutable_subject":true,
#  "sub_claim_prefix":"repo:<owner>@<owner_id>/<repo>@<repo_id>"}
```

Con ese prefijo, crea la credencial para la rama `main`:

```bash
PREFIX=$(gh api "repos/$REPO/actions/oidc/customization/sub" --jq .sub_claim_prefix)

cat > fic-main.json <<EOF
{
  "name": "gh-main-branch",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "$PREFIX:ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}
EOF
az ad app federated-credential create --id "$APP_ID" --parameters fic-main.json
```

### 4. Secretos de GitHub (solo identificadores)

```bash
gh secret set AZURE_CLIENT_ID       -R "$REPO" -b "$APP_ID"
gh secret set AZURE_TENANT_ID       -R "$REPO" -b "$(az account show --query tenantId -o tsv)"
gh secret set AZURE_SUBSCRIPTION_ID -R "$REPO" -b "$SUB"
```

Ninguno de los tres valores permite autenticarse por sí solo. Se guardan como secretos para que GitHub los enmascare (`***`) en los logs de un repositorio público. También podrían ser variables (`vars.*`).

### 5. Desplegar y comprobar

```bash
gh workflow run deploy.yml -R "$REPO" --ref main
curl -i "https://$APP.azurewebsites.net/alive"   # 200 Healthy
```

La primera petición puede tardar más de un minuto: el plan F1 no tiene Always On y la app arranca en frío.

## Subject inmutable: el detalle que rompe los tutoriales

La mayoría de las guías usan un subject como este:

```text
repo:<owner>/<repo>:ref:refs/heads/main
```

Los repositorios creados después del **15 de julio de 2026** reciben por defecto **subjects inmutables**, que incluyen los IDs numéricos del propietario y del repositorio:

```text
repo:<owner>@<owner_id>/<repo>@<repo_id>:ref:refs/heads/main
```

Entra ID compara el subject carácter por carácter. Si la credencial federada usa el formato antiguo, el login falla con `AADSTS700213` aunque el job se ejecute en `main`.

**No escribas el subject a mano: cópialo del log.** `azure/login@v3` imprime el token que presenta:

```text
Federated token details:
 issuer - https://token.actions.githubusercontent.com
 subject claim - repo:<owner>@<owner_id>/<repo>@<repo_id>:ref:refs/heads/main
 audience - api://AzureADTokenExchange
```

La línea `subject claim - …` es exactamente lo que debe ir en el campo **Subject** de la credencial federada.

**Renombrar el repositorio cambia el subject.** Los IDs numéricos no cambian, pero el nombre sigue formando parte del subject. Después de renombrar este repositorio, la credencial con el nombre anterior dejó de coincidir (`AADSTS700213`) y hubo que actualizarla con el nombre nuevo.

## `environment:` cambia el subject

Si el job declara un entorno:

```yaml
jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: production
```

el subject deja de contener la rama:

```text
repo:<owner>@<owner_id>/<repo>@<repo_id>:environment:production
```

La credencial de `main` ya no sirve, aunque el job siga ejecutándose en `main`. Hace falta una segunda credencial federada:

```bash
cat > fic-env.json <<EOF
{
  "name": "gh-env-production",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "$PREFIX:environment:production",
  "audiences": ["api://AzureADTokenExchange"]
}
EOF
az ad app federated-credential create --id "$APP_ID" --parameters fic-env.json
```

Este repositorio tiene las dos credenciales: `gh-main-branch` y `gh-env-production`. Ten en cuenta que referenciar `environment: production` crea el entorno en GitHub automáticamente, pero **sin reglas de protección**. Si quieres aprobaciones manuales, configúralas en *Settings → Environments*.

## Errores comunes

| Error | Causa | Solución |
|---|---|---|
| `Unable to get ACTIONS_ID_TOKEN_REQUEST_URL env variable` / `Please make sure to give write permissions to id-token in the workflow` | Falta `permissions: id-token: write`. Sin ese permiso, el runner no puede pedir el token OIDC. | Añade `id-token: write` y `contents: read`. Al declarar `permissions`, el resto de permisos del `GITHUB_TOKEN` pasan a `none`. |
| `AADSTS700213: No matching federated identity record found for presented assertion subject '…'` | El subject del token no coincide con ninguna credencial federada: formato antiguo frente a inmutable, otra rama, un `environment:` o un repositorio renombrado. | Copia el subject de la línea `subject claim - …` del log de `azure/login` y crea o actualiza la credencial federada. |
| `AADSTS700212: No matching federated identity record found for presented assertion audience '…'` | La audiencia del token no coincide con `audiences` de la credencial. | Usa la audiencia por defecto `api://AzureADTokenExchange` (no pases `audience:` a `azure/login` salvo que la credencial use otra). |
| `AADSTS70025: The client '***' has no configured federated identity credentials` | La credencial federada se acaba de crear y aún no se ha propagado. | Espera uno o dos minutos y vuelve a ejecutar el job. |

Los cambios en credenciales federadas pueden tardar en propagarse. En este laboratorio, un job falló con `AADSTS700213` un minuto después de que otro con el mismo subject hubiera funcionado; el reintento posterior funcionó.

## Estructura relevante

```text
.github/workflows/deploy.yml                    # workflow activo (OIDC)
docs/workflows-antes/deploy-client-secret.yml   # workflow anterior (client secret), solo referencia
src/CleanArchitecture.Presentation/             # API que se despliega
```

## Documentación oficial

- GitHub: [OpenID Connect en GitHub Actions](https://docs.github.com/en/actions/concepts/security/openid-connect)
- GitHub: [Configurar OpenID Connect en Azure](https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-in-azure)
- GitHub Changelog: [Immutable subject claims for GitHub Actions OIDC tokens](https://github.blog/changelog/2026-04-23-immutable-subject-claims-for-github-actions-oidc-tokens)
- Microsoft Learn: [Workload identity federation](https://learn.microsoft.com/entra/workload-id/workload-identity-federation)
- Microsoft Learn: [Configurar una app para confiar en un proveedor de identidad externo](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-create-trust)
- Microsoft Learn: [Desplegar en App Service con GitHub Actions](https://learn.microsoft.com/azure/app-service/deploy-github-actions)
- [`azure/login`](https://github.com/Azure/login)

## Licencia

[MIT](LICENSE), igual que el proyecto original.
