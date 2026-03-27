#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/tests/PbxAdmin.LoadTests"

AGENTS="${1:-5}"
DURATION="${2:-3}"
CATEGORY="${3:-all}"
OUTPUT_DIR="$REPO_ROOT/tests/sdk-scenario-results"

RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
MAGENTA='\033[0;35m'
BOLD='\033[1m'
DIM='\033[2m'
NC='\033[0m'

PASSED=0
FAILED=0
TOTAL=0

mkdir -p "$OUTPUT_DIR"

print_header() {
    echo ""
    echo -e "${CYAN}================================================================${NC}"
    echo -e "${CYAN}  SDK Test Platform — Scenario Runner${NC}"
    echo -e "${CYAN}================================================================${NC}"
    echo ""
    echo -e "  Agents    : ${BOLD}$AGENTS${NC}"
    echo -e "  Duration  : ${BOLD}$DURATION min${NC} por escenario"
    echo -e "  Category  : ${BOLD}$CATEGORY${NC}"
    echo -e "  Output    : ${BOLD}$OUTPUT_DIR${NC}"
    echo ""
}

print_usage() {
    echo "Uso: $0 [agents] [duration] [category]"
    echo ""
    echo "  agents    Numero de agentes SIP (default: 5)"
    echo "  duration  Duracion en minutos por escenario (default: 3)"
    echo "  category  Categoria a ejecutar (default: all)"
    echo ""
    echo "Categorias disponibles:"
    echo "  all         Todos los escenarios (22)"
    echo "  functional  Solo funcionales (12)"
    echo "  sdk         Solo validacion SDK (3)"
    echo "  load        Solo carga (3)"
    echo "  chaos       Solo chaos (3)"
    echo "  soak        Solo soak (1) — requiere --duration alto"
    echo "  smoke       Solo el smoke test (1)"
    echo ""
    echo "Ejemplos:"
    echo "  $0                    # 5 agentes, 3 min, todos"
    echo "  $0 10 5 functional   # 10 agentes, 5 min, solo funcionales"
    echo "  $0 5 3 sdk           # 5 agentes, 3 min, solo SDK"
    echo "  $0 20 60 load        # 20 agentes, 60 min, solo carga"
    echo ""
}

print_section() {
    local title="$1"
    echo -e "${MAGENTA}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo -e "${MAGENTA}  $title${NC}"
    echo -e "${MAGENTA}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo ""
}

wait_for_channels_drained() {
    local max_wait=60
    local elapsed=0
    while [ "$elapsed" -lt "$max_wait" ]; do
        local count
        count=$(docker exec demo-pbx-realtime asterisk -rx "core show channels count" 2>/dev/null \
            | head -1 | grep -oP '^\d+' || echo "0")
        if [ "$count" = "0" ]; then
            return
        fi
        echo -e "  ${DIM}Esperando que $count channels drenen en Asterisk...${NC}"
        sleep 3
        elapsed=$((elapsed + 3))
    done
    echo -e "  ${YELLOW}Timeout esperando drain — continuando${NC}"
}

run_scenario() {
    local name="$1"
    local title="$2"
    local description="$3"

    # Wait for leftover channels from previous scenario to drain
    if [ "$TOTAL" -gt 0 ]; then
        wait_for_channels_drained
    fi

    TOTAL=$((TOTAL + 1))

    echo -e "${YELLOW}────────────────────────────────────────────────────────────────${NC}"
    echo -e "${BOLD}  $title${NC}"
    echo -e "${YELLOW}────────────────────────────────────────────────────────────────${NC}"
    echo ""
    echo -e "  ${DIM}$description${NC}"
    echo ""
    echo -e "  Ejecutando: ${CYAN}--scenario $name --agents $AGENTS --duration $DURATION${NC}"
    echo ""

    local output_file="$OUTPUT_DIR/${name}.json"
    local start_time
    start_time=$(date +%s)

    if dotnet run --project "$PROJECT" -- \
        --scenario "$name" \
        --agents "$AGENTS" \
        --duration "$DURATION" \
        --output "$output_file" 2>&1; then
        local end_time
        end_time=$(date +%s)
        local elapsed=$((end_time - start_time))
        echo ""
        echo -e "  ${GREEN}PASSED${NC} en ${elapsed}s — reporte: $output_file"
        PASSED=$((PASSED + 1))
    else
        local end_time
        end_time=$(date +%s)
        local elapsed=$((end_time - start_time))
        echo ""
        echo -e "  ${RED}FAILED${NC} en ${elapsed}s"
        FAILED=$((FAILED + 1))
    fi
    echo ""
}

print_summary() {
    echo -e "${CYAN}================================================================${NC}"
    echo -e "${CYAN}  Resultados${NC}"
    echo -e "${CYAN}================================================================${NC}"
    echo ""
    echo -e "  Total     : $TOTAL escenarios"
    echo -e "  ${GREEN}Passed${NC}    : $PASSED"
    echo -e "  ${RED}Failed${NC}    : $FAILED"
    echo ""

    if [ "$FAILED" -eq 0 ]; then
        echo -e "  ${GREEN}${BOLD}Todos los escenarios pasaron.${NC}"
    else
        echo -e "  ${RED}${BOLD}$FAILED escenario(s) fallaron.${NC}"
    fi

    echo ""
    echo -e "  Reportes JSON en: $OUTPUT_DIR/"
    echo ""
}

# ─── Scenario groups ──────────────────────────────────────────────────────────

run_functional() {
    print_section "FUNCIONALES — Validacion de features individuales"

    run_scenario "inbound-answer" \
        "Inbound Answer" \
        "3 llamadas entrantes a la cola loadtest (ext 105). Los agentes contestan.
  Valida CDR disposition=ANSWERED, secuencia CEL completa (CHAN_START, ANSWER,
  BRIDGE_ENTER, HANGUP), y eventos de cola (ENTERQUEUE, CONNECT)."

    run_scenario "inbound-busy" \
        "Inbound Busy" \
        "5 llamadas rapidas a ext 200 con menos agentes que llamadas.
  Valida que se generen disposiciones mixtas: ANSWERED para las que los
  agentes alcanzan a contestar, NO ANSWER/BUSY para las demas."

    run_scenario "queue-distribution" \
        "Queue Distribution" \
        "10 llamadas secuenciales a ext 200 (1 por segundo) con 5 agentes.
  Valida distribucion round-robin: los CDR deben mostrar distintos
  dstchannel, comprobando que Asterisk rota entre agentes."

    run_scenario "ivr-navigation" \
        "IVR Navigation" \
        "1 llamada a ext 200 (entrada IVR). Valida que el CEL muestre
  APP_START con aplicaciones de IVR (Playback/Background) en la ruta
  de la llamada, confirmando que el caller paso por el menu."

    run_scenario "transfer" \
        "Blind Transfer" \
        "1 llamada a ext 200, el agente contesta y hace blind transfer.
  Valida 2 legs CDR con el mismo linkedId y evento BLINDTRANSFER
  en el CEL, confirmando que la transferencia se ejecuto."

    run_scenario "hold" \
        "Hold / Unhold" \
        "1 llamada a ext 200, el agente contesta, pone en hold y resume.
  Valida eventos HOLD/UNHOLD en el CEL y que la duracion del CDR
  incluye el tiempo en hold."

    run_scenario "conference" \
        "Conference Bridge" \
        "2 llamadas a ext 801 (sala de conferencia). Valida CDR para
  ambas llamadas y eventos BRIDGE_ENTER en el CEL mostrando que
  ambos participantes entraron al bridge."

    run_scenario "parking" \
        "Call Parking" \
        "1 llamada a ext 200, el agente contesta y estaciona la llamada.
  Valida evento PARK_START en el CEL y existencia del CDR,
  confirmando que el parking slot fue asignado."

    run_scenario "voicemail" \
        "Voicemail" \
        "1 llamada a ext 200 sin agentes disponibles. Espera ring timeout
  y caida a voicemail. Valida CDR y que el CEL muestre la aplicacion
  VoiceMail en la ruta de la llamada."

    run_scenario "dtmf" \
        "DTMF Detection" \
        "1 llamada a ext 1006 (escenario DTMF del PSTN emulator).
  Valida CDR=ANSWERED y que el CEL muestre la ruta de la llamada
  a traves del PSTN emulator con deteccion de tonos."

    run_scenario "outbound-call" \
        "Outbound Call" \
        "Un agente origina una llamada saliente a 1001 (PSTN normal answer).
  Valida CDR src=agente, dst=1001, disposition=ANSWERED.
  Confirma el ruteo de llamadas salientes."

    run_scenario "time-condition" \
        "Time Condition" \
        "1 llamada a ext 200 ruteada a traves de condicion de tiempo.
  Valida existencia del CDR y que el CEL muestre el contexto de
  time condition en la ruta, confirmando el ruteo por horario."
}

run_sdk() {
    print_section "SDK — Validacion de las librerias Asterisk.Sdk"

    run_scenario "sdk-session-accuracy" \
        "SDK Session Accuracy" \
        "Genera 10 llamadas en 3 fases: 5 contestadas (ext 105), 3 timeout
  (ext 105 con agentes pausados via AMI QueuePause), 2 fallidas (ext 999
  → Congestion). Compara CallSession vs CDR para cada disposicion:
  Completed↔ANSWERED, TimedOut↔NO ANSWER, Failed↔BUSY/FAILED.
  Valida duracion (+/- 2s) y caller number. Detecta bugs en la maquina
  de estados del SDK."

    run_scenario "sdk-live-drift" \
        "SDK Live State Drift" \
        "Genera una rafaga sostenida de llamadas (5 cada 10s durante 2 minutos)
  mientras muestrea el estado en vivo del SDK (AsteriskServer.Channels) vs
  el estado real de Asterisk (AMI 'core show channels count') cada 3 segundos.
  Valida que la diferencia (drift) sea menor al 5%. Detecta bugs de
  sincronizacion en el tracking de canales del SDK."

    run_scenario "sdk-reconnect" \
        "SDK Auto-Reconnect" \
        "Genera 2 llamadas para verificar la conexion, luego envia 'manager reload'
  por AMI para forzar una desconexion. Espera hasta 10s para que el SDK se
  reconecte automaticamente, y despues genera 2 llamadas mas para verificar
  que el tracking de sesiones sigue funcionando."
}

run_load() {
    print_section "CARGA — Comportamiento bajo estres"

    run_scenario "ramp-up" \
        "Ramp Up" \
        "Incrementa linealmente las llamadas concurrentes de 0 al target durante
  el periodo de ramp-up. Mide como degrada el SDK bajo carga creciente.
  Valida que el answer rate se mantenga estable mientras la carga sube."

    run_scenario "sustained-load" \
        "Sustained Load" \
        "Mantiene N llamadas concurrentes constantes durante toda la duracion.
  Valida estabilidad: answer rate, tiempos de respuesta, y que no haya
  degradacion progresiva bajo carga sostenida."

    run_scenario "peak-hour" \
        "Peak Hour" \
        "Simula las horas pico de una oficina colombiana con patrones de llamada
  realistas y mix de escenarios (60% normal, 10% cortas, 5% largas,
  5% transferencias, etc.). El test mas cercano a produccion."
}

run_chaos() {
    print_section "CHAOS — Resiliencia y recuperacion"

    run_scenario "agent-crash" \
        "Agent Crash" \
        "Mata registros SIP de agentes aleatoriamente durante llamadas activas.
  Valida que el SDK detecte la desconexion de agentes, re-distribuya
  las llamadas, y no deje canales huerfanos."

    run_scenario "trunk-failure" \
        "Trunk Failure" \
        "Detiene el PSTN emulator a mitad del test para simular caida de trunk.
  Valida que el SDK maneje la falla gracefully: detecta la desconexion,
  reporta errores correctamente, y no se queda colgado."

    run_scenario "rapid-reregister" \
        "Rapid Re-register" \
        "Registra y desregistra agentes SIP rapidamente para estresar el tracking
  de endpoints del SDK. Valida que Asterisk y el SDK manejen la rafaga
  de registros sin perder estado ni dejar registros fantasma."
}

run_soak() {
    print_section "SOAK — Estabilidad a largo plazo"

    run_scenario "eight-hour-soak" \
        "Eight Hour Soak" \
        "Carga moderada sostenida por 8 horas para detectar memory leaks,
  file descriptor leaks, y agotamiento de recursos. Monitorea GC,
  conteo de canales, y estabilidad del answer rate a lo largo del
  tiempo. Requiere --duration alto (480 min)."
}

# ─── Main ─────────────────────────────────────────────────────────────────────

if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; then
    print_usage
    exit 0
fi

print_header

echo -e "${BOLD}Verificando que Docker stack este corriendo...${NC}"
if ! docker ps --format '{{.Names}}' 2>/dev/null | grep -q "demo-pbx-realtime"; then
    echo -e "${RED}El Docker stack no esta corriendo.${NC}"
    echo -e "Ejecuta primero:"
    echo -e "  cd docker && docker compose -f docker-compose.pbxadmin.yml up -d"
    echo ""
    exit 1
fi
echo -e "${GREEN}Docker stack detectado.${NC}"
echo ""

case "$CATEGORY" in
    all)
        run_functional
        run_sdk
        run_load
        run_chaos
        # soak excluido de 'all' por su duracion (8 horas)
        ;;
    functional)
        run_functional
        ;;
    sdk)
        run_sdk
        ;;
    load)
        run_load
        ;;
    chaos)
        run_chaos
        ;;
    soak)
        run_soak
        ;;
    smoke)
        print_section "SMOKE — Test rapido"
        run_scenario "inbound-answer" \
            "Smoke Test (Inbound Answer)" \
            "3 llamadas entrantes a la cola loadtest (ext 105). Test minimo para
  verificar que el stack Docker, la generacion de llamadas, los agentes
  SIP, y la validacion CDR/CEL funcionan correctamente."
        ;;
    *)
        echo -e "${RED}Categoria desconocida: $CATEGORY${NC}"
        echo ""
        print_usage
        exit 1
        ;;
esac

print_summary

exit "$FAILED"
