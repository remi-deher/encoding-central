-- Local join preflight; never reconciled. Public identity metadata only, no credentials.
CREATE TABLE cluster_join_user_collisions (
    local_user_id TEXT PRIMARY KEY REFERENCES users(id),
    expected_username TEXT NOT NULL,
    host_username TEXT NOT NULL,
    proposed_username TEXT NOT NULL,
    reserved_names TEXT NOT NULL,
    cluster_id TEXT NOT NULL
);
