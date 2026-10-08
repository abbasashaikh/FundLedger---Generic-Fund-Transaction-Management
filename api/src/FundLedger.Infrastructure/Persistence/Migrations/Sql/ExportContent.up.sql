-- Phase 4: finished export files are kept on the job row until they expire (24 h), then emptied.
ALTER TABLE fl.export_jobs ADD COLUMN IF NOT EXISTS content   bytea;
ALTER TABLE fl.export_jobs ADD COLUMN IF NOT EXISTS file_name varchar(120);
