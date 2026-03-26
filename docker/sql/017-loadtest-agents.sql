-- Load test agents: 300 PJSIP endpoints (2100-2399) for load testing agent pool
-- These endpoints are used by the AgentPoolService to simulate high-volume call scenarios
-- Qualify frequency is disabled (0) to avoid 300 simultaneous OPTIONS requests during scale testing

-- ps_endpoints: 300 load test agents (2100-2399)
INSERT INTO ps_endpoints (id, transport, aors, auth, context, disallow, allow, direct_media, callerid) VALUES (
    SELECT
        (2100 + i)::text AS id,
        'transport-udp' AS transport,
        (2100 + i)::text AS aors,
        (2100 + i)::text AS auth,
        'default' AS context,
        'all' AS disallow,
        'ulaw,alaw' AS allow,
        'no' AS direct_media,
        format('"Load Agent %s" <%s>', i + 1, 2100 + i) AS callerid
    FROM generate_series(0, 299) AS t(i)
)
ON CONFLICT (id) DO NOTHING;

-- ps_auths: 300 load test agent credentials
INSERT INTO ps_auths (id, auth_type, password, username) VALUES (
    SELECT
        (2100 + i)::text AS id,
        'userpass' AS auth_type,
        format('loadtest%s', 2100 + i) AS password,
        (2100 + i)::text AS username
    FROM generate_series(0, 299) AS t(i)
)
ON CONFLICT (id) DO NOTHING;

-- ps_aors: 300 load test agent address of records (no qualify to avoid OPTIONS flood)
INSERT INTO ps_aors (id, max_contacts, remove_existing, qualify_frequency) VALUES (
    SELECT
        (2100 + i)::text AS id,
        1 AS max_contacts,
        'yes' AS remove_existing,
        0 AS qualify_frequency
    FROM generate_series(0, 299) AS t(i)
)
ON CONFLICT (id) DO NOTHING;

-- loadtest queue: dedicated queue for load testing
INSERT INTO queue_table (name, strategy, timeout, ringinuse, wrapuptime, servicelevel, maxlen) VALUES
    ('loadtest', 'leastrecent', 15, 'no', 5, 20, 0)
ON CONFLICT (name) DO NOTHING;

-- Queue members: distribute 300 agents across three tiers by penalty
-- Tier 1 (2100-2199): penalty 0 (primary)
-- Tier 2 (2200-2299): penalty 1 (secondary)
-- Tier 3 (2300-2399): penalty 2 (overflow)
INSERT INTO queue_members (queue_name, interface, membername, penalty) VALUES (
    SELECT
        'loadtest' AS queue_name,
        format('PJSIP/%s', 2100 + i) AS interface,
        format('Load Agent %s', i + 1) AS membername,
        CASE
            WHEN i < 100 THEN 0
            WHEN i < 200 THEN 1
            ELSE 2
        END AS penalty
    FROM generate_series(0, 299) AS t(i)
)
ON CONFLICT DO NOTHING;

-- Also add all 300 agents to sales queue with same penalty distribution
INSERT INTO queue_members (queue_name, interface, membername, penalty) VALUES (
    SELECT
        'sales' AS queue_name,
        format('PJSIP/%s', 2100 + i) AS interface,
        format('Load Agent %s', i + 1) AS membername,
        CASE
            WHEN i < 100 THEN 0
            WHEN i < 200 THEN 1
            ELSE 2
        END AS penalty
    FROM generate_series(0, 299) AS t(i)
)
ON CONFLICT DO NOTHING;
