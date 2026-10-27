-- ============================================================================
-- Migration 020 — drop settings rows written by the broken Backups page save
--
-- The Backups page sent `PUT /api/settings` with a `{ category, key, value }`
-- body. The endpoint takes a flat "category.key" map and filed every name
-- without a dot under `general`, so each save wrote `general.category`,
-- `general.key` and `general.value` instead of `backup.*`. The endpoint now
-- rejects such names; this removes the rows already written.
-- ============================================================================

DELETE FROM settings
WHERE category = 'general' AND key IN ('category', 'key', 'value');
