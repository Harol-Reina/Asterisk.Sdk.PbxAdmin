# WebRTC Audio with Asterisk in Containers: A Comprehensive Guide

> **Audience:** DevOps engineers, Asterisk administrators, and developers deploying Asterisk PBX with WebRTC softphones inside Docker Compose or Kubernetes environments.
>
> **Last updated:** 2026-03-25

---

## Table of Contents

1. [The Core Problem](#1-the-core-problem)
2. [How WebRTC Media Negotiation Works](#2-how-webrtc-media-negotiation-works)
3. [Why Containers Break It](#3-why-containers-break-it)
4. [Solution 1: ice_host_candidates (No TURN Required)](#4-solution-1-ice_host_candidates-no-turn-required)
5. [Solution 2: macvlan / ipvlan Networks (No NAT at All)](#5-solution-2-macvlan--ipvlan-networks-no-nat-at-all)
6. [Solution 3: Host Network Mode](#6-solution-3-host-network-mode)
7. [Solution 4: TURN Relay (coturn and Alternatives)](#7-solution-4-turn-relay-coturn-and-alternatives)
8. [Kubernetes-Specific Solutions](#8-kubernetes-specific-solutions)
9. [TURN Latency: Myth vs Reality](#9-turn-latency-myth-vs-reality)
10. [Comparison Table](#10-comparison-table)
11. [Recommendations by Scenario](#11-recommendations-by-scenario)
12. [PbxAdmin Docker Stack: Current vs Recommended](#12-pbxadmin-docker-stack-current-vs-recommended)
13. [References](#13-references)

---

## 1. The Core Problem

When a WebRTC browser calls an Asterisk server running inside a Docker container, the audio (RTP) path must be negotiated using ICE (Interactive Connectivity Establishment). Asterisk, inside the container, only knows its container-private IP address (e.g., `172.18.0.3`). When it sends its ICE candidates to the browser, it advertises this unreachable private IP. The browser cannot send RTP packets to `172.18.0.3` -- that address does not exist on the browser's network. Result: **one-way audio or no audio at all**.

This is fundamentally a **NAT traversal** problem. The container's network namespace acts as a NAT boundary, identical to the classic "Asterisk behind a firewall" scenario but with one key difference: the NAT is local and fully under your control.

---

## 2. How WebRTC Media Negotiation Works

WebRTC uses ICE to discover a working media path between two endpoints. The process:

1. **Candidate gathering** -- Each side collects candidate addresses:
   - **Host candidates** -- Local IP addresses directly on the machine
   - **Server-reflexive candidates** -- External IP discovered via STUN
   - **Relay candidates** -- Allocated address on a TURN server

2. **Connectivity checks** -- Both sides test candidate pairs using STUN binding requests, in priority order: host > server-reflexive > relay.

3. **Candidate selection** -- The pair with the best connectivity and lowest latency wins.

4. **DTLS-SRTP handshake** -- Encryption keys are exchanged over the winning candidate pair.

5. **Media flows** -- Encrypted RTP (SRTP) packets flow over the selected path.

For Asterisk specifically:
- ICE is mandatory for WebRTC (`ice_support=yes` on the endpoint).
- Asterisk uses `res_rtp_asterisk` for ICE, STUN, and TURN client functionality.
- STUN/TURN server addresses are configured globally in `rtp.conf`.
- Transport-level NAT settings (`external_media_address`, `external_signaling_address`) affect SDP but **not** ICE candidates directly.

---

## 3. Why Containers Break It

### Docker bridge networking (the default)

```
Browser (192.168.1.50) <---> Docker Host (192.168.1.10) <---> Container (172.18.0.3)
                                    |--- port mapping ---|
```

- Asterisk sees its own IP as `172.18.0.3`.
- ICE host candidates advertise `172.18.0.3`.
- The browser receives `172.18.0.3` as a candidate and cannot reach it.
- `external_media_address` in `pjsip.conf` rewrites the SDP `c=` line, but **ICE candidates in the SDP `a=candidate` lines are generated separately by `res_rtp_asterisk`** and are NOT affected by `external_media_address`.
- Without STUN, TURN, or `ice_host_candidates`, the only candidate the browser receives is the unreachable container IP.

### The SDP vs ICE distinction (critical)

This is the single most misunderstood aspect. There are two separate address mechanisms in WebRTC SDP:

| Mechanism | Config | Affects |
|-----------|--------|---------|
| SDP connection address (`c=`) | `external_media_address` in pjsip.conf transport | Non-ICE endpoints only |
| ICE candidates (`a=candidate:`) | `rtp.conf` -- stunaddr, turnaddr, ice_host_candidates | WebRTC endpoints (ICE mandatory) |

**For WebRTC, only the ICE candidates matter.** The `external_media_address` setting is effectively ignored because ICE takes precedence over the SDP connection address.

---

## 4. Solution 1: ice_host_candidates (No TURN Required)

### What it does

The `[ice_host_candidates]` section in `rtp.conf` tells Asterisk: "When you would advertise container IP X as a host candidate, advertise host IP Y instead (or in addition)." This maps the container's private IP to the Docker host's real IP at the ICE level.

### History

- Introduced in **Asterisk 13.7.0** (2016, patch by Sean Bright, reviewed by Joshua Colp).
- Available in all current Asterisk versions (18, 20, 21, 22).
- Designed specifically for static one-to-one NAT scenarios -- exactly what Docker bridge networking is.

### Configuration

```ini
; /etc/asterisk/rtp.conf

[general]
rtpstart=20000
rtpend=20050
; Do NOT set stunaddr or turnaddr when using ice_host_candidates
; icesupport is enabled by default since Asterisk 13

[ice_host_candidates]
; Format: <container IP> => <host IP>[,include_local_address]
;
; Replace the container's private IP with the Docker host's LAN IP.
; This is the IP that browsers on your network can reach.
172.18.0.3 => 192.168.1.10
```

With the optional `include_local_address` flag:

```ini
[ice_host_candidates]
; Advertise BOTH addresses -- useful if some clients are on the Docker
; network (e.g., other containers) and some are external (browsers).
172.18.0.3 => 192.168.1.10,include_local_address
```

### How to determine the container IP

The container IP can change on restart. Two approaches:

**Option A: Fixed IP in Docker Compose**

```yaml
networks:
  pbxnet:
    driver: bridge
    ipam:
      config:
        - subnet: 172.28.0.0/16

services:
  asterisk-realtime:
    networks:
      pbxnet:
        ipv4_address: 172.28.0.10
```

**Option B: Entrypoint script discovers it dynamically**

```sh
#!/bin/sh
CONTAINER_IP=$(hostname -i | awk '{print $1}')
HOST_IP=${EXTERNAL_IP:?EXTERNAL_IP must be set}

# Write ice_host_candidates dynamically
cat >> /etc/asterisk/rtp.conf <<EOF

[ice_host_candidates]
${CONTAINER_IP} => ${HOST_IP}
EOF

exec /usr/sbin/asterisk -f
```

### Advantages

- **Zero latency overhead** -- media flows directly between browser and Docker host (port-mapped to container). No relay hop.
- **No extra services** -- no coturn, no STUN server, no additional containers.
- **Simple** -- one section in rtp.conf.

### Limitations

- Requires a **static, known host IP** (or dynamic entrypoint scripting).
- Only works when the Docker host IP is reachable from the browser's network. Does NOT work for clients coming from the internet through a second NAT (e.g., remote users behind their own home router connecting to your office Docker host).
- The container IP must be predictable or discovered at startup.

### When it is enough

- LAN-only deployments (office softphones, demo environments).
- VPN users who have direct routing to the Docker host.
- Development and staging environments.

### When it is NOT enough

- Public internet users behind symmetric NAT.
- Mobile users on carrier-grade NAT (CGNAT).
- Any scenario where the browser cannot directly reach the Docker host IP.

---

## 5. Solution 2: macvlan / ipvlan Networks (No NAT at All)

### Concept

Instead of Docker's default bridge network (which creates a NAT boundary), `macvlan` and `ipvlan` drivers give each container its own IP address **on the host's physical network**. The container appears as a separate device on the LAN, with no NAT, no port mapping.

```
Browser (192.168.1.50)     Asterisk container (192.168.1.20)
         \                        /
          --- LAN switch (L2) ---
```

Since there is no NAT, Asterisk's host candidates advertise `192.168.1.20` directly. The browser can reach it. **No TURN, no STUN, no ice_host_candidates needed.**

### Docker Compose configuration

```yaml
networks:
  asterisk-lan:
    driver: macvlan
    driver_opts:
      parent: eth0        # Host's physical interface
    ipam:
      config:
        - subnet: 192.168.1.0/24
          gateway: 192.168.1.1
          ip_range: 192.168.1.20/29   # Reserve .20-.27 for containers

services:
  asterisk-realtime:
    image: asterisk:22
    networks:
      asterisk-lan:
        ipv4_address: 192.168.1.20
    # No port mapping needed! Container IS on the LAN.

  asterisk-file:
    image: asterisk:22
    networks:
      asterisk-lan:
        ipv4_address: 192.168.1.21
    # Each instance gets its own IP -- no port conflicts.
    # Both can use port 5060, 8089, etc.
```

### macvlan vs ipvlan

| Feature | macvlan | ipvlan |
|---------|---------|--------|
| MAC address | Unique per container | Shared with host |
| Switch compatibility | Requires promiscuous mode or no MAC limits | Works with port-security switches |
| DHCP | Works (unique MAC) | Does not work (shared MAC) |
| Host-to-container | Requires bridge shim | Requires bridge shim |
| Recommended for | Physical hardware, home labs | Cloud VMs, managed switches |

### The host-to-container caveat

With both macvlan and ipvlan, **the Docker host itself cannot communicate with its own macvlan containers** (Linux kernel restriction). This means:

- The PbxAdmin Blazor app (if running on the same Docker host) cannot reach Asterisk via the macvlan IP.
- Workaround: Add a second network (bridge) for internal communication between PbxAdmin and Asterisk, and use macvlan only for external-facing traffic (SIP, RTP, WebSocket).

```yaml
services:
  asterisk-realtime:
    networks:
      asterisk-lan:           # macvlan -- browsers reach this
        ipv4_address: 192.168.1.20
      internal:               # bridge -- PbxAdmin reaches this

  pbx-admin:
    networks:
      internal:               # bridge -- talks to Asterisk on internal net
    ports:
      - "8080:8080"           # Exposed normally for browser access
```

### Advantages

- **True zero-NAT** -- no NAT traversal needed at all.
- **Multiple instances on same ports** -- each container has its own IP, so all can bind to 5060/udp, 8089/tcp, etc.
- **No TURN, no STUN, no ice_host_candidates** -- Asterisk's native IPs are directly reachable.
- **No RTP port mapping** -- RTP ports do not need to be mapped because the container IS on the network.

### Limitations

- **Host-to-container isolation** -- requires dual-network workaround.
- **IP address management** -- you must reserve a range of LAN IPs for containers.
- **Cloud incompatibility** -- most cloud providers (AWS, GCP, Azure) do not allow macvlan on their virtual NICs. Only works on bare metal or VMs with promiscuous mode enabled.
- **Slightly more complex** networking setup.
- **Same LAN requirement** -- still only works for clients that can reach the LAN IP. Remote internet users still need TURN.

---

## 6. Solution 3: Host Network Mode

### Concept

With `network_mode: host`, the container shares the host's network stack entirely. No NAT, no port mapping -- the container IS the host, network-wise.

```yaml
services:
  asterisk:
    image: asterisk:22
    network_mode: host
```

Asterisk binds directly to the host's interfaces. Its host candidates are the host's actual IPs. WebRTC works exactly as if Asterisk were installed natively.

### The multi-instance problem

**You cannot run multiple Asterisk instances on the same host with `network_mode: host`** because they would all try to bind to the same ports (5060, 8089, 20000-20050, etc.).

Workarounds:
- Configure each Asterisk instance to use different ports (SIP on 5060 vs 5061, RTP ranges 20000-20050 vs 20100-20150, WSS on 8089 vs 8190). This is what PbxAdmin already does with bridge networking.
- But with host networking, you lose container isolation benefits.

### Advantages

- **Simplest networking** -- zero configuration needed for NAT traversal.
- **Best performance** -- no iptables overhead, no userspace proxy.
- **No TURN/STUN needed** for LAN clients.

### Limitations

- **Single instance per host** (unless ports are manually separated).
- **No network isolation** -- container can see and bind any host port.
- **Port conflicts** with other services on the host.
- **Not suitable for multi-tenant** or multi-instance deployments.

---

## 7. Solution 4: TURN Relay (coturn and Alternatives)

### When TURN is genuinely required

TURN is the **only solution** that works in ALL network scenarios, including:

- Browser behind symmetric NAT (many corporate firewalls).
- Browser on mobile carrier (CGNAT -- carrier-grade NAT).
- Asterisk behind NAT and browser behind NAT simultaneously (double-NAT).
- Any situation where direct connectivity between Asterisk and the browser is impossible.

**TURN is a fallback of last resort in ICE**, used only when host and server-reflexive candidates both fail. If you configure `ice_host_candidates` correctly and the browser can reach the host IP, TURN will never be used even if configured.

### Does Asterisk have a built-in TURN server?

**No.** Asterisk has a built-in TURN *client* (in `res_rtp_asterisk`) that can obtain relay candidates from an external TURN server. Asterisk does not and cannot act as a TURN server itself. You always need a separate TURN server process.

### coturn

The most widely deployed open-source TURN server. Mature, battle-tested, supports TURN over UDP/TCP/TLS/DTLS.

```yaml
# Docker Compose
coturn:
  image: coturn/coturn:alpine
  network_mode: host
  command:
    - --listening-port=3478
    - --min-port=49152
    - --max-port=49200
    - --realm=pbxadmin
    - --user=pbxadmin:pbxadmin
    - --lt-cred-mech
    - --fingerprint
    - --no-tls
    - --no-dtls
    - --log-file=stdout
```

### Alternatives to coturn

| Server | Language | Notes |
|--------|----------|-------|
| **coturn** | C | Industry standard, 15+ years, large feature set, 116 CVEs in Docker image scan |
| **eturnal** | Erlang | Simple config (YAML), easy to install, lightweight, good for small deployments |
| **STUNner** | Go | Kubernetes-native, Helm chart, auto-scaling, designed for cloud. Zero CVEs in scan |
| **Violet** | C | Ultra-lightweight, based on libjuice, minimal dependencies, still young (v0.4) |
| **Pion TURN** | Go | Part of the Pion WebRTC ecosystem, good for Go developers who want custom logic |

**For Kubernetes deployments, STUNner is the strongest choice** -- it integrates with the Kubernetes Gateway API and can scale horizontally.

**For Docker Compose, coturn or eturnal are the pragmatic choices.** coturn is better documented; eturnal is simpler to configure.

### rtp.conf configuration for TURN

```ini
[general]
rtpstart=20000
rtpend=20050
stunaddr=turn.example.com:3478
turnaddr=turn.example.com:3478
turnusername=pbxadmin
turnpassword=pbxadmin
```

### Combining TURN with ice_host_candidates

You can use **both** -- but Asterisk's documentation explicitly warns against it:

> *"If you do define anything in [ice_host_candidates], you almost certainly will NOT want to specify 'stunaddr' or 'turnaddr'."*

The reason: `ice_host_candidates` replaces the host candidate IPs. If you also configure STUN, Asterisk will gather server-reflexive candidates pointing to the same IP (since STUN discovers the host's public IP, which is the same as what `ice_host_candidates` already advertises). This creates redundant candidates. And if you also configure TURN, every call will allocate a TURN relay port even when it is never used -- wasting resources on the TURN server.

**Best practice:** Use `ice_host_candidates` for LAN clients and deploy TURN only when you also need to serve remote/internet clients. In that case, configure only `stunaddr` and `turnaddr` (not `ice_host_candidates`), and let STUN discover the host IP automatically.

---

## 8. Kubernetes-Specific Solutions

### The challenge

Kubernetes adds layers of abstraction (pod network, service mesh, ingress) that make RTP connectivity harder than Docker Compose. Key issues:

- Pods get cluster-internal IPs (e.g., `10.244.0.15`) -- not reachable from outside.
- ClusterIP services work for TCP but are problematic for UDP RTP with large port ranges.
- NodePort services can expose UDP but add another NAT layer.
- LoadBalancer services vary by cloud provider; many do not support UDP well.

### Option A: hostNetwork (simplest)

```yaml
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      hostNetwork: true
      containers:
        - name: asterisk
          image: asterisk:22
```

**Pros:** Pod uses the node's IP directly. WebRTC works like bare metal.

**Cons:**
- Only ONE Asterisk pod per node (port conflicts).
- Use a DaemonSet if you want one per node.
- No network isolation.
- Cannot run 2+ Asterisk instances with the same port on one node.

### Option B: NodePort for signaling + hostPort for RTP

```yaml
apiVersion: v1
kind: Service
metadata:
  name: asterisk-ws
spec:
  type: NodePort
  ports:
    - port: 8089
      nodePort: 30089
      protocol: TCP
---
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      containers:
        - name: asterisk
          ports:
            - containerPort: 20000
              hostPort: 20000
              protocol: UDP
            # ... repeat for RTP range
```

**Pros:** Signaling through NodePort, RTP through hostPort.

**Cons:** You must map every RTP port as a hostPort. With a 50-port range, that is 50 port declarations. Only one pod per node.

### Option C: MetalLB (bare-metal LoadBalancer)

For on-premise Kubernetes clusters, MetalLB assigns real LAN IPs to LoadBalancer services:

```yaml
apiVersion: metallb.io/v1beta1
kind: IPAddressPool
metadata:
  name: asterisk-pool
spec:
  addresses:
    - 192.168.1.30-192.168.1.35

---
apiVersion: v1
kind: Service
metadata:
  name: asterisk-lb
spec:
  type: LoadBalancer
  loadBalancerIP: 192.168.1.30
  ports:
    - name: wss
      port: 8089
      protocol: TCP
    - name: sip
      port: 5060
      protocol: UDP
    # RTP ports -- MetalLB handles UDP
```

**Pros:** Each Asterisk instance gets a real LAN IP. No NAT. Multiple instances possible.

**Cons:** Only for bare-metal clusters. Cloud clusters use their own LB (which may not support UDP ranges well).

### Option D: Calico/Cilium with BGP

Advanced CNIs can advertise pod IPs via BGP to your network router, making pod IPs directly routable:

```yaml
apiVersion: crd.projectcalico.org/v1
kind: IPPool
metadata:
  name: asterisk-pool
spec:
  cidr: 10.10.10.0/29
  natOutgoing: false      # No NAT -- direct routing
```

**Pros:** True direct routing. Each pod gets a routable IP. Best for large deployments.

**Cons:** Requires BGP-capable router. Complex to set up. Overkill for small deployments.

### Option E: STUNner (Kubernetes-native TURN)

STUNner is purpose-built for this problem. It runs as a Kubernetes Gateway, terminates TURN, and routes media into the cluster:

```
Browser --> STUNner Gateway (TURN) --> Asterisk Pod
```

Deploy via Helm:
```sh
helm repo add stunner https://l7mp.io/stunner
helm install stunner-gateway stunner/stunner-gateway-operator
```

**Pros:** Cloud-native, scales horizontally, integrates with Gateway API. Works on any cloud.

**Cons:** Adds a relay hop (like coturn). Still has TURN latency overhead.

### Kubernetes recommendation

| Scenario | Recommended approach |
|----------|---------------------|
| Single Asterisk, bare metal | hostNetwork |
| Multiple Asterisk, bare metal | MetalLB |
| Single Asterisk, cloud | hostNetwork + ice_host_candidates (node IP) |
| Multiple Asterisk, cloud | STUNner |
| Any scenario with internet users | STUNner or external coturn |

---

## 9. TURN Latency: Myth vs Reality

### Connection setup latency

TURN adds latency to **call setup** because ICE must:
1. Gather relay candidates from the TURN server (one STUN Allocate round-trip).
2. Test connectivity on relay candidate pairs.
3. These tests have lower priority, so they run last.

Typical ICE gathering overhead with TURN: **100-300 ms** additional setup time compared to host-only candidates.

### Media relay latency

Once a call is established through a TURN relay, every RTP packet takes an extra hop:

```
Without TURN:  Browser <---> Asterisk          (1 hop)
With TURN:     Browser <---> TURN <---> Asterisk  (2 hops)
```

Measured overhead depends on where the TURN server is:

| TURN location | Additional RTT | Perceptible? |
|---------------|---------------|--------------|
| Same host (localhost) | < 1 ms | No |
| Same LAN | 0.5-2 ms | No |
| Same datacenter | 1-5 ms | No |
| Same region (cloud) | 5-20 ms | Barely |
| Cross-region | 30-100 ms | Yes |
| Cross-continent | 100-200 ms | Clearly yes |

### The PbxAdmin situation

In the PbxAdmin Docker stack, coturn runs on the **same host** (`network_mode: host`). The relay overhead is effectively **< 1 ms** per hop. The perceived "slow connection" is almost certainly from **ICE gathering and connectivity checks**, not from media relay latency.

ICE with TURN configured will:
1. Gather host candidates (~instant)
2. Gather server-reflexive candidates via STUN (~50-100 ms RTT)
3. Gather relay candidates via TURN Allocate (~50-100 ms RTT)
4. Run connectivity checks on all pairs (~100-500 ms)

**Without TURN** (using `ice_host_candidates` instead):
1. Gather host candidates with mapped IPs (~instant)
2. Run connectivity checks (~50-100 ms)

The difference: **~200-500 ms faster call setup** by eliminating STUN/TURN gathering phases.

---

## 10. Comparison Table

| Approach | TURN needed? | Extra services | Multi-instance | Internet clients | Setup complexity | Media latency | Call setup speed |
|----------|-------------|----------------|----------------|-----------------|-----------------|---------------|-----------------|
| **ice_host_candidates** | No | None | Yes (different ports) | No (LAN only) | Low | Zero overhead | Fast |
| **macvlan/ipvlan** | No | None | Yes (different IPs, same ports) | No (LAN only) | Medium | Zero overhead | Fast |
| **Host network** | No | None | No (1 per host) | No (LAN only) | Lowest | Zero overhead | Fastest |
| **TURN (coturn)** | Yes | coturn container | Yes | Yes | Medium | < 1 ms (same host) | Slower (+200-500 ms setup) |
| **K8s hostNetwork** | No | None | No (1 per node) | No (LAN only) | Low | Zero overhead | Fast |
| **K8s MetalLB** | No | MetalLB | Yes | No (LAN only) | Medium | Zero overhead | Fast |
| **K8s STUNner** | Yes (built-in) | STUNner pods | Yes | Yes | Medium-High | ~1-5 ms (in-cluster) | Slower (+200-500 ms) |

---

## 11. Recommendations by Scenario

### Development (local Docker Compose, single developer)

**Use `ice_host_candidates` with fixed container IPs.**

- No extra services needed.
- Fast call setup.
- Set `EXTERNAL_IP` to your machine's LAN IP.
- Remove `stunaddr` and `turnaddr` from `rtp.conf`.

### Demo / Staging (Docker Compose, team on same LAN)

**Use `ice_host_candidates` as primary; add coturn as optional fallback.**

- `ice_host_candidates` handles LAN users with zero overhead.
- If some team members connect over VPN or remote, keep coturn available.
- Configure the softphone client to use STUN only (not TURN) as the default, with TURN as fallback.

### Production -- LAN only (office PBX, all users on local network)

**Use macvlan networking.**

- Each Asterisk instance gets a real LAN IP.
- No NAT traversal configuration needed at all.
- Cleanest networking model.
- Requires IP address planning.

### Production -- Internet users (remote workers, mobile softphones)

**TURN is mandatory. No way around it.**

- Remote users behind CGNAT or symmetric NAT cannot be reached without relay.
- Deploy coturn on a server with a public IP (or same host with port forwarding).
- Consider eturnal for simpler configuration.
- Use `ice_host_candidates` additionally so that LAN users get the fast path while remote users fall back to TURN.

  > **Note:** You CAN combine `ice_host_candidates` with TURN if you accept the documentation warning. Configure TURN in the softphone client's ICE configuration (browser-side) rather than in Asterisk's `rtp.conf`. This way, Asterisk advertises mapped host candidates and the browser uses TURN only if those candidates fail.

### Kubernetes -- Small deployment (1-3 Asterisk instances)

**Use hostNetwork with one pod per node.**

- Simplest Kubernetes approach.
- Use DaemonSet or anti-affinity to prevent port conflicts.

### Kubernetes -- Large deployment (many instances, cloud)

**Use STUNner as the TURN gateway.**

- Kubernetes-native, scales with the cluster.
- Integrates with Gateway API.
- Works on any cloud provider.

---

## 12. PbxAdmin Docker Stack: Current vs Recommended

### Current configuration

```
Browser --> coturn (TURN relay, host network) --> Asterisk container (bridge network)
```

- `rtp.conf`: stunaddr + turnaddr pointing to coturn.
- `pjsip.conf`: external_media_address + external_signaling_address set to host IP.
- coturn runs on host network mode.
- All media relayed through coturn even for LAN clients.

### Recommended: ice_host_candidates (LAN-only demo)

**Changes to `rtp.conf`:**

```ini
[general]
rtpstart=20000
rtpend=20050
; Remove stunaddr and turnaddr
; icesupport is on by default

[ice_host_candidates]
; Container IP => Host LAN IP
; Use fixed IPs in Docker Compose or dynamic entrypoint
172.28.0.10 => ${EXTERNAL_IP}
```

**Changes to `docker-compose.pbxadmin.yml`:**

```yaml
networks:
  pbxnet:
    driver: bridge
    ipam:
      config:
        - subnet: 172.28.0.0/16

services:
  # Remove coturn service entirely (for LAN-only demo)

  asterisk-realtime:
    networks:
      pbxnet:
        ipv4_address: 172.28.0.10
    # ... rest unchanged

  asterisk-file:
    networks:
      pbxnet:
        ipv4_address: 172.28.0.11
    # ... rest unchanged
```

**Changes to entrypoint:**

```sh
#!/bin/sh
CONTAINER_IP=$(hostname -i | awk '{print $1}')

# Replace ice_host_candidates placeholder
if [ -n "$EXTERNAL_IP" ]; then
    # Existing pjsip.conf substitutions...

    # Add ice_host_candidates to rtp.conf
    cat >> /etc/asterisk/rtp.conf <<EOF

[ice_host_candidates]
${CONTAINER_IP} => ${EXTERNAL_IP}
EOF
fi

exec /usr/sbin/asterisk -f
```

**Result:** Calls connect ~200-500 ms faster. No coturn service needed. Simpler stack.

### Alternative: macvlan (cleanest for multi-instance)

```yaml
networks:
  asterisk-lan:
    driver: macvlan
    driver_opts:
      parent: eth0
    ipam:
      config:
        - subnet: 192.168.1.0/24
          gateway: 192.168.1.1
          ip_range: 192.168.1.20/29
  internal:
    driver: bridge

services:
  asterisk-realtime:
    networks:
      asterisk-lan:
        ipv4_address: 192.168.1.20
      internal:
    # No port mapping. No rtp.conf NAT config. No ice_host_candidates.
    # Asterisk just works because it has a real LAN IP.
```

---

## 13. References

### Official Asterisk Documentation

- [Configuring Asterisk for WebRTC Clients](https://docs.asterisk.org/Configuration/WebRTC/Configuring-Asterisk-for-WebRTC-Clients/)
- [ICE in Asterisk](https://docs.asterisk.org/Configuration/Miscellaneous/Interactive-Connectivity-Establishment-ICE-in-Asterisk/)
- [Configuring res_pjsip to work through NAT](https://docs.asterisk.org/Configuration/Channel-Drivers/SIP/Configuring-res_pjsip/Configuring-res_pjsip-to-work-through-NAT/)
- [rtp.conf.sample (Asterisk source)](https://github.com/asterisk/asterisk/blob/master/configs/samples/rtp.conf.sample)
- [res_pjsip module configuration](https://docs.asterisk.org/Latest_API/API_Documentation/Module_Configuration/res_pjsip/)

### Asterisk Community Discussions

- [Asterisk in Docker RTP packet issues](https://community.asterisk.org/t/asteris-within-the-docker-doesnt-properly-sent-rtp-packets/102595/2) -- Joshua Colp's guidance on Docker + WebRTC
- [WebRTC RTP / ICE candidate selection issue](https://community.asterisk.org/t/webrtc-rtp-issue-asterisk-ice-candidate-selection/73393)
- [Asterisk 18 WebRTC in Docker](https://community.asterisk.org/t/asterisk-18-webrtc-in-docker/101681)
- [Asterisk 20.4.0 with STUN/TURN function](https://community.asterisk.org/t/asterisk-20-4-0-with-stun-turn-function/102769)

### ice_host_candidates History

- [Original code review: Allow ICE host candidates to be overridden (Asterisk 13)](http://lists.digium.com/pipermail/asterisk-code-review/2016-February/015801.html)

### Docker Networking

- [Docker macvlan network driver](https://docs.docker.com/engine/network/drivers/macvlan/)
- [Docker ipvlan network driver](https://docs.docker.com/engine/network/drivers/ipvlan/)
- [Docker Compose macvlan example](https://github.com/sarunas-zilinskas/docker-compose-macvlan)
- [How to Dockerize an RTC App](https://idomagor.medium.com/how-to-dockerize-a-rtc-app-or-service-41368ea7b31)

### Kubernetes

- [Running Asterisk on Kubernetes (growse.com)](https://www.growse.com/2021/09/17/asterisk-on-kubernetes-part-1.html)
- [CyCoreSystems Asterisk Kubernetes config](https://github.com/CyCoreSystems/asterisk-config)
- [STUNner: Kubernetes-native TURN](https://medium.com/l7mp-technologies/open-source-turn-server-showdown-coturn-vs-stunner-da3a02a2fc9d)

### TURN Servers

- [coturn](https://github.com/coturn/coturn)
- [eturnal](https://blog.wirelessmoves.com/2025/05/a-new-stun-turn-server-with-eturnal-in-5-minutes.html)
- [STUNner Helm chart](https://l7mp.io/stunner)
- [Self-hosted STUN/TURN setup guide](https://webrtc.ventures/2025/01/how-to-set-up-self-hosted-stun-turn-servers-for-webrtc-applications/)
- [Coturn alternatives](https://alternativeto.net/software/coturn/)

### WebRTC and ICE

- [ICE in WebRTC: Server Setup and Performance](https://webrtc.ventures/2022/04/ice-in-webrtc/)
- [WebRTC STUN vs TURN](https://getstream.io/resources/projects/webrtc/advanced/stun-turn/)
- [Troubleshooting WebRTC ICE Candidates](https://moldstud.com/articles/p-troubleshooting-webrtc-ice-candidates-common-issues-and-solutions-explained)
