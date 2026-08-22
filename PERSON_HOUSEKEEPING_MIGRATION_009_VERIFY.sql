SELECT version,applied_utc,description
FROM archive_schema_migration WHERE version=9;

PRAGMA table_info(housekeeping_recommendation_action);
PRAGMA index_list(housekeeping_recommendation_action);
PRAGMA foreign_key_check(housekeeping_recommendation_action);

SELECT action_type,count(*) actions
FROM housekeeping_recommendation_action
WHERE run_id=(SELECT max(run_id) FROM housekeeping_run WHERE status='completed')
GROUP BY action_type ORDER BY action_type;

SELECT r.recommendation_id,r.person_emby_id,r.recommendation_type,
       group_concat(a.action_order||':'||a.action_type||':'||a.summary,' | ') action_plan
FROM housekeeping_recommendation r
JOIN housekeeping_recommendation_action a ON a.recommendation_id=r.recommendation_id
WHERE r.run_id=(SELECT max(run_id) FROM housekeeping_run WHERE status='completed')
GROUP BY r.recommendation_id ORDER BY r.recommendation_id;

-- No standalone rename may remain for a person participating in a merge/split.
SELECT ren.recommendation_id rename_id,ren.person_emby_id,parent.recommendation_id parent_id,parent.recommendation_type
FROM housekeeping_recommendation ren JOIN housekeeping_recommendation parent
  ON parent.run_id=ren.run_id AND parent.recommendation_type IN('review-merge','review-split')
 AND (parent.person_emby_id=ren.person_emby_id OR (','||coalesce(parent.current_value,'')||',') LIKE '%,'||ren.person_emby_id||',%')
WHERE ren.run_id=(SELECT max(run_id) FROM housekeeping_run WHERE status='completed')
  AND ren.recommendation_type='rename-person';
