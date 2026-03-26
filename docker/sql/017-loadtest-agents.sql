-- Load test agents: 300 PJSIP endpoints (2100-2399) for load testing agent pool
-- These endpoints are used by the AgentPoolService to simulate high-volume call scenarios
-- Qualify frequency is disabled (0) to avoid 300 simultaneous OPTIONS requests during scale testing

-- ps_endpoints: 300 load test agents (2100-2399)
INSERT INTO ps_endpoints (id, transport, aors, auth, context, disallow, allow, direct_media, callerid)
SELECT
    (2100 + i)::text,
    'transport-udp',
    (2100 + i)::text,
    (2100 + i)::text,
    'default',
    'all',
    'ulaw,alaw',
    'no',
    format('"Load Agent %s" <%s>', i + 1, 2100 + i)
FROM generate_series(0, 299) AS t(i)
ON CONFLICT (id) DO NOTHING;

-- ps_auths: 300 load test agent credentials
INSERT INTO ps_auths (id, auth_type, password, username)
SELECT
    (2100 + i)::text,
    'userpass',
    format('loadtest%s', 2100 + i),
    (2100 + i)::text
FROM generate_series(0, 299) AS t(i)
ON CONFLICT (id) DO NOTHING;

-- ps_aors: 300 load test agent address of records (no qualify to avoid OPTIONS flood)
INSERT INTO ps_aors (id, max_contacts, remove_existing, qualify_frequency)
SELECT
    (2100 + i)::text,
    1,
    'yes',
    0
FROM generate_series(0, 299) AS t(i)
ON CONFLICT (id) DO NOTHING;

-- loadtest queue: dedicated queue for load testing
INSERT INTO queue_table (name, strategy, timeout, ringinuse, wrapuptime, servicelevel, maxlen) VALUES
    ('loadtest', 'leastrecent', 15, 'no', 5, 20, 0)
ON CONFLICT (name) DO NOTHING;

-- Queue members: distribute 300 agents across three tiers by penalty
-- Tier 1 (2100-2199): penalty 0 (primary)
-- Tier 2 (2200-2299): penalty 1 (secondary)
-- Tier 3 (2300-2399): penalty 2 (overflow)
INSERT INTO queue_members (queue_name, interface, membername, penalty)
SELECT
    'loadtest',
    format('PJSIP/%s', 2100 + i),
    format('Load Agent %s', i + 1),
    CASE
        WHEN i < 100 THEN 0
        WHEN i < 200 THEN 1
        ELSE 2
    END
FROM generate_series(0, 299) AS t(i)
ON CONFLICT DO NOTHING;

-- Also add all 300 agents to sales queue with same penalty distribution
INSERT INTO queue_members (queue_name, interface, membername, penalty)
SELECT
    'sales',
    format('PJSIP/%s', 2100 + i),
    format('Load Agent %s', i + 1),
    CASE
        WHEN i < 100 THEN 0
        WHEN i < 200 THEN 1
        ELSE 2
    END
FROM generate_series(0, 299) AS t(i)
ON CONFLICT DO NOTHING;
