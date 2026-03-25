#!/bin/sh
# Entrypoint wrapper for Asterisk Docker containers.
# Replaces ${EXTERNAL_IP} placeholders in pjsip.conf and rtp.conf
# with the actual host IP for WebRTC NAT traversal.

replace_placeholders() {
    local file="$1"
    [ ! -f "$file" ] && return
    cp "$file" /tmp/_ast_tmp
    if [ -n "$EXTERNAL_IP" ]; then
        sed "s/\${EXTERNAL_IP}/$EXTERNAL_IP/g" /tmp/_ast_tmp > "$file"
    else
        grep -v '\${EXTERNAL_IP}' /tmp/_ast_tmp > "$file"
    fi
    rm -f /tmp/_ast_tmp
}

replace_placeholders /etc/asterisk/pjsip.conf
replace_placeholders /etc/asterisk/rtp.conf

echo "[entrypoint] EXTERNAL_IP=${EXTERNAL_IP:-<not set>}"

exec /usr/sbin/asterisk -f
