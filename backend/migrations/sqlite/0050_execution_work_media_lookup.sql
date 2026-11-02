-- Local runtime work: find an active duplicate by library and normalized media key.
-- Keep execution_id in the index to cover the join without reading the work row.
CREATE INDEX ix_execution_work_library_media
    ON execution_work(library_id, media_key, execution_id);
