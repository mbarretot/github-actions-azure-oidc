# Guía de despliegue con Azure OIDC

[← Volver al README](../README.md)

## Antes de empezar

Necesitas Bash, Azure CLI (`az`), GitHub CLI (`gh`) y un fork o copia de este repositorio con Actions habilitado. Usa `az login` y `gh auth login` en tu equipo antes de empezar. El runner instala el SDK de [`global.json`](../global.json); no necesitas .NET local para ejecutar este despliegue desde GitHub.

Tu cuenta de preparación debe poder crear recursos de Azure, una aplicación de Entra y su service principal, y asignar roles RBAC. Son permisos de preparación: **no** se conceden al pipeline. Consulta los [permisos de administración de credenciales federadas](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-create-trust).

> [!WARNING]
> Los siguientes comandos crean recursos en tu suscripción. F1 está sujeto a disponibilidad y cuota; cambiar a un plan de pago genera costes. Revisa el SKU y las [tarifas de App Service](https://azure.microsoft.com/pricing/details/app-service/linux/) antes de continuar. No ejecutes esta guía sobre recursos compartidos.

Mantén la misma sesión de Bash para conservar las variables de los pasos siguientes. Los valores entre `<...>` son tuyos; no uses los recursos del autor.

## 1. Infraestructura

```bash
SUB="<id-de-tu-suscripcion>"
RG="rg-github-oidc-demo-$RANDOM"
LOCATION="centralus"
PLAN="asp-github-oidc-demo-$RANDOM"
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

Después, cambia `AZURE_WEBAPP_NAME` en [`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml) por el valor de `$APP` en tu fork. Publica ese cambio en `main` antes de lanzar el despliegue; si el push inicia un job antes de terminar la configuración, es esperable que falle.

Este ajuste de migraciones solo permite arrancar la API para comprobar su disponibilidad. No configura la base de datos ni sustituye la configuración de autenticación de la aplicación.

## 2. App registration, service principal y rol

```bash
APP_ID=$(az ad app create --display-name github-actions-azure-oidc \
  --sign-in-audience AzureADMyOrg --query appId -o tsv)
SP_ID=$(az ad sp create --id "$APP_ID" --query id -o tsv)
WEBAPP_ID=$(az webapp show -n "$APP" -g "$RG" --query id -o tsv)

az role assignment create \
  --assignee-object-id "$SP_ID" --assignee-principal-type ServicePrincipal \
  --role "Website Contributor" --scope "$WEBAPP_ID"
```

El rol [Website Contributor](https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/web-and-mobile#website-contributor) permite gestionar el sitio, no solo publicar paquetes. Su alcance se limita aquí a `$WEBAPP_ID`; no le asignes alcance de suscripción. Si reutilizas una identidad, revisa sus otras asignaciones: esta guía no elimina permisos previos.

## 3. Credencial federada

Primero consulta el prefijo real del subject de tu repositorio:

```bash
gh api "repos/$REPO/actions/oidc/customization/sub"
# {"use_default":true,"use_immutable_subject":true,
#  "sub_claim_prefix":"repo:<owner>@<owner_id>/<repo>@<repo_id>"}
```

El workflow activo usa **`environment: production`**. Crea la credencial para ese entorno:

```bash
PREFIX=$(gh api "repos/$REPO/actions/oidc/customization/sub" --jq .sub_claim_prefix)
# Comprueba que PREFIX contiene un prefijo real, no una cadena vacía ni "null".
printf '%s\n' "$PREFIX"

cat > fic-production.json <<EOF
{
  "name": "gh-env-production",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "$PREFIX:environment:production",
  "audiences": ["api://AzureADTokenExchange"]
}
EOF
az ad app federated-credential create --id "$APP_ID" --parameters fic-production.json
```

Si el endpoint no devuelve `sub_claim_prefix`, no continúes con un valor vacío: consulta la configuración OIDC del repositorio y contrasta el subject con el log de `azure/login`. Si usas una plantilla de subject personalizada, utiliza el subject completo que esa plantilla emite.

En GitHub, revisa **Settings → Environments → production**. Restringe las ramas de despliegue a `main` y añade aprobaciones si tu plan las admite. El subject de entorno no contiene la rama: la restricción de ramas debe aplicarse en GitHub.

## 4. Secretos de GitHub (solo identificadores)

```bash
gh secret set AZURE_CLIENT_ID       -R "$REPO" -b "$APP_ID"
gh secret set AZURE_TENANT_ID       -R "$REPO" -b "$(az account show --query tenantId -o tsv)"
gh secret set AZURE_SUBSCRIPTION_ID -R "$REPO" -b "$SUB"
```

Ninguno de los tres valores permite autenticarse por sí solo. Se guardan como secretos para que GitHub los enmascare (`***`) en los logs de un repositorio público. También podrían ser variables (`vars.*`).

## 5. Desplegar y comprobar

```bash
gh workflow run deploy.yml -R "$REPO" --ref main
gh run list -R "$REPO" --workflow deploy.yml --limit 5
# Espera a que el run termine correctamente antes de comprobar la aplicación.
curl -i "https://$APP.azurewebsites.net/alive"   # 200 Healthy
```

En F1 puede haber arranque en frío. La respuesta esperada es `200 Healthy`; `/alive` comprueba la vida del proceso, no la conectividad a PostgreSQL ni la autorización de usuarios. El log del run permite distinguir errores de compilación, login y publicación.

## Subject inmutable: comprueba el valor real

El formato tradicional de un subject de rama es:

```text
repo:<owner>/<repo>:ref:refs/heads/main
```

Según el [anuncio de GitHub](https://github.blog/changelog/2026-04-23-immutable-subject-claims-for-github-actions-oidc-tokens/), los repositorios nuevos y los renombrados o transferidos después del **15 de julio de 2026** adoptan el formato con IDs inmutables:

```text
repo:<owner>@<owner_id>/<repo>@<repo_id>:ref:refs/heads/main
```

El subject configurado debe coincidir exactamente con el emitido. Si la credencial federada usa el formato antiguo, el login falla con `AADSTS700213` aunque el job se ejecute en `main`.

**No escribas el subject a mano: cópialo del log.** `azure/login@v3` muestra los metadatos de la identidad federada, no es necesario imprimir el JWT completo:

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

Una credencial de rama no coincide con ese subject. La credencial `gh-env-production` del paso 3 ya cubre el entorno del workflow actual.

La guía crea únicamente `gh-env-production`, porque es la que utiliza el workflow activo. No necesitas añadir una credencial de rama salvo que quieras ejecutar otro job sin `environment:`. Referenciar un entorno no sustituye configurar sus reglas de protección en **Settings → Environments**.

## Errores comunes

| Error | Causa | Solución |
|---|---|---|
| `Unable to get ACTIONS_ID_TOKEN_REQUEST_URL env variable` / `Please make sure to give write permissions to id-token in the workflow` | Falta `permissions: id-token: write`. Sin ese permiso, el runner no puede pedir el token OIDC. | Añade `id-token: write` y `contents: read`. Al declarar `permissions`, el resto de permisos del `GITHUB_TOKEN` pasan a `none`. |
| `AADSTS700213: No matching federated identity record found for presented assertion subject '…'` | El subject del token no coincide con ninguna credencial federada: formato antiguo frente a inmutable, otra rama, un `environment:` o un repositorio renombrado. | Copia el subject de la línea `subject claim - …` del log de `azure/login` y crea o actualiza la credencial federada. |
| `AADSTS700212: No matching federated identity record found for presented assertion audience '…'` | La audiencia del token no coincide con `audiences` de la credencial. | Usa la audiencia por defecto `api://AzureADTokenExchange` (no pases `audience:` a `azure/login` salvo que la credencial use otra). |
| `AADSTS70025` | La aplicación indicada no tiene credenciales federadas disponibles; puede ser una app equivocada, una credencial ausente o propagación pendiente. | Verifica el client ID y lista sus credenciales; si acabas de crear una, espera y reintenta. |

Los cambios pueden tardar en propagarse. Antes de reintentar, comprueba issuer, subject, audiencia y client ID; un reintento no corrige una confianza mal configurada.

## Límites de seguridad

![Tres pabellones conectados representan emisión, validación y uso de una identidad temporal](assets/oidc/trust-journey.jpg)

_Ilustración conceptual; el diagrama de secuencia del README describe el intercambio real._

- El token OIDC del job y el token de acceso de Azure son credenciales diferentes. No asumas que tienen la misma duración ni los publiques en logs.
- `id-token: write` permite solicitar el token OIDC; no concede permisos sobre Azure por sí solo.
- Revisa las reglas de `production`, la protección de `main` y las asignaciones RBAC. Para producción, considera fijar las acciones a hashes de commit revisados.
- La federación del pipeline no implementa autenticación de usuarios, acceso a datos ni seguridad de red de la API.
- La [guía de API](api-guide.md) y las [decisiones de arquitectura](adr/README.md) documentan la aplicación base; no son pasos que este workflow ejecute.

## Limpieza

Ejecuta esto **solo sobre el laboratorio que acabas de crear**, conservando las variables originales. No apunta a los recursos del autor.

```bash
# Falla si perdiste las variables de esta sesión.
: "${RG:?Falta RG}" "${SUB:?Falta SUB}" "${APP_ID:?Falta APP_ID}" "${REPO:?Falta REPO}"

# Revisa primero la suscripción y los recursos que vas a eliminar.
az account show --query '{subscription:id,name:name}' -o table
az resource list --resource-group "$RG" --query '[].{name:name,type:type}' -o table

# Elimina TODO el grupo dedicado al laboratorio (incluido su App Service Plan).
az group delete --name "$RG" --subscription "$SUB"

# Elimina solo la app registration dedicada creada en el paso 2.
az ad app delete --id "$APP_ID"

# Retira los identificadores de tu fork si no vas a reutilizarlo.
gh secret delete AZURE_CLIENT_ID -R "$REPO"
gh secret delete AZURE_TENANT_ID -R "$REPO"
gh secret delete AZURE_SUBSCRIPTION_ID -R "$REPO"
rm -f fic-production.json
```

Si reutilizaste recursos o una app registration existente, **no ejecutes estas eliminaciones**: retira únicamente los recursos y la credencial federada que pertenezcan a este ejercicio. La eliminación del grupo no elimina por sí sola la app registration de Entra.

[← Volver al README](../README.md)
