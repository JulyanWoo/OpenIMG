<p align="center">
  <strong style="font-size: 2.2em;">OpenIMG</strong><br>
  <em>Restauración fotográfica conservadora de mobiliario para Windows</em>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-x64-0078D4?style=for-the-badge&logo=windows&logoColor=white" alt="Windows x64" />
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Release-0.1.0--rc.3-orange?style=for-the-badge" alt="Release candidate 0.1.0-rc.3" />
</p>

OpenIMG es una aplicación de escritorio WPF para mejorar fotografías de muebles sin perder de vista la estructura física que aparece en la imagen original. Combina análisis local, restauración asistida por GPT, validación automática y revisión humana antes de exportar.

> **Estado:** versión candidata `0.1.0-rc.3`. La prueba de aceptación en una máquina limpia aún no se ha completado. La publicación del código no implica que el software esté listo para uso crítico ni constituye una garantía de calidad de restauración.

## Flujo de trabajo

```text
Importar fotografía
        ↓
Verificar original y analizar localmente
        ↓
Restaurar con GPT
        ↓
Validar con Barrier 3
        ↓
Revisar original y resultado
        ↓
Exportar explícitamente
```

## Funciones principales

- **Restauración con GPT:** utiliza la superficie web de GPT en WebView2 con una sesión iniciada por el usuario.
- **Análisis local:** prepara evidencia sobre la imagen y el mueble para el flujo de restauración.
- **Validación independiente:** Barrier 3 clasifica cada resultado como `Accepted`, `NeedsReview` o `Rejected`; esa clasificación no reemplaza el criterio humano.
- **Revisión y comparación:** permite inspeccionar el original junto al candidato restaurado antes de decidir.
- **Exportación explícita:** el resultado no se aprueba ni se exporta automáticamente. El original se conserva como fuente inmutable.
- **Datos locales:** la base de datos, la configuración y el perfil autenticado del navegador se guardan en el espacio de datos del usuario.

El detector local opcional no forma parte de este snapshot. La aplicación puede compilarse y arrancar sin ese modelo; su ausencia no sustituye ni habilita la restauración GPT.

## Requisitos

- Windows x64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) para compilar desde el código fuente.
- Microsoft Edge WebView2 Runtime para usar la superficie web de GPT.
- Una cuenta de ChatGPT con acceso a las funciones necesarias. La disponibilidad y los límites dependen de la cuenta y del servicio.

## Compilar y ejecutar

Desde PowerShell, en la raíz del repositorio:

```powershell
dotnet restore OpenIMG.slnx
dotnet build OpenIMG.slnx --no-restore
dotnet run --project .\src\OpenIMG.App\OpenIMG.App.csproj
```

Para ejecutar las pruebas deterministas rápidas:

```powershell
.\scripts\test-fast.ps1
```

La verificación normal no ejecuta operaciones en vivo con GPT ni requiere fotografías o archivos de calibración privados. Consulta [CONTRIBUTING.md](CONTRIBUTING.md) antes de añadir pruebas o datos.

## Datos y privacidad

De forma predeterminada, OpenIMG guarda configuración, base de datos, registros, operaciones y perfil de WebView2 bajo `%LocalAppData%\OpenIMG`. No añadas al repositorio fotografías personales o de clientes, bases de datos, registros, perfiles del navegador, credenciales, telemetría de proveedor ni resultados generados.

La cuenta de GPT se autentica dentro de WebView2. La sesión es local al usuario y no debe copiarse ni compartirse. Antes de enviar una imagen a GPT, verifica que tienes autorización para procesarla con un servicio externo.

## Estructura del repositorio

| Ruta | Contenido |
|---|---|
| `src/OpenIMG.App` | Aplicación de escritorio WPF y automatización del proveedor web |
| `src/OpenIMG.Application` | Casos de uso, orquestación, políticas y validación |
| `src/OpenIMG.Core` | Entidades, contratos y modelos de dominio |
| `src/OpenIMG.Infrastructure` | Persistencia y análisis/procesamiento local |
| `tests/OpenIMG.Tests` | Pruebas deterministas del producto |
| `scripts` | Comandos de verificación para desarrollo |

## Licencia y distribución

El código propio de OpenIMG se publica bajo la licencia MIT; consulta [LICENSE](LICENSE). Las dependencias conservan sus propias licencias y avisos, descritos en [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). El modelo YOLO y el icono local están excluidos de este snapshot y no deben añadirse a un paquete público sin resolver por separado sus condiciones y procedencia. La documentación de [licenciamiento](LICENSING.md) resume ese alcance.

## Aviso

La restauración de imágenes puede alterar detalles visuales y no debe tratarse como evidencia del estado real de un objeto. Conserva el original y revisa cada candidato antes de utilizarlo o compartirlo.
