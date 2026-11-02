-- Rows logged before the danger map was corrected keep the old level:
-- wdc_query is a read-only SELECT, wdc_stop_service ends a running service.
UPDATE mcp_tool_calls SET danger_level = 'read'
 WHERE tool_name = 'wdc_query' AND danger_level <> 'read';
UPDATE mcp_tool_calls SET danger_level = 'destructive'
 WHERE tool_name = 'wdc_stop_service' AND danger_level = 'mutate';
