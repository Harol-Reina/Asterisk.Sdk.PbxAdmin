-- Load test queue definition.
-- PJSIP endpoints and queue members are provisioned dynamically by the test platform
-- (AgentProvisioningService) based on --agents N. No static agents are pre-loaded.

INSERT INTO queue_table (name, strategy, timeout, ringinuse, wrapuptime, servicelevel, maxlen) VALUES
    ('loadtest', 'rrmemory', 15, 'no', 5, 20, 0)
ON CONFLICT (name) DO NOTHING;
