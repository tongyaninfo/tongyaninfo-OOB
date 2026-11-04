namespace OobManagement.Infrastructure.Persistence.Migrations;

/// <summary>把两种设备百分比归并到同一业务指标；由显式 EF 迁移或隔离事务测试调用。</summary>
public static class UnifiedFanPercentageSql
{
    // 不创建表、列或临时函数；调用方必须先停止旧采集器，并保存可恢复的迁移前备份。
    public const string Apply = """
        LOCK TABLE "OOB_METRIC_SERIES", "OOB_METRIC_SAMPLE", "OOB_SERVER", "OOB_USER_PREFERENCE"
            IN SHARE ROW EXCLUSIVE MODE;

        DO $unify_fan_percentage$
        DECLARE
            sensor record;
            server_row record;
            preference_row record;
            table_entry record;
            target_id bigint;
            target_name text;
            merged_metrics jsonb;
            merged_capabilities jsonb;
            settings jsonb;
            original_tables jsonb;
            merged_tables jsonb;
            target_key text;
            legacy_key text;
            canonical_setting jsonb;
            legacy_setting jsonb;
            table_setting jsonb;
            columns_setting jsonb;
            percent_setting jsonb;
            pwm_setting jsonb;
            speed_setting jsonb;
        BEGIN
            -- 仅合并完全相同的服务器及稳定资源键，不猜测历史风扇键之间的对应关系。
            FOR sensor IN
                SELECT "ServerId" AS server_id, "SensorKey" AS sensor_key,
                    min("Id") FILTER (WHERE "MetricCode" = 'fan.percent') AS percent_id,
                    min("Id") FILTER (WHERE "MetricCode" = 'fan.pwm.percent') AS pwm_id
                FROM "OOB_METRIC_SERIES"
                WHERE "MetricCode" IN ('fan.percent', 'fan.pwm.percent')
                GROUP BY "ServerId", "SensorKey"
            LOOP
                target_id := coalesce(sensor.percent_id, sensor.pwm_id);
                SELECT regexp_replace("Name",
                    '(设备转速百分比|裝置轉速百分比|转速百分比|轉速百分比|PWM占空比|PWM佔空比)([（(]%[）)])?$', '', 'i')
                    || '转速百分比（%）' INTO target_name
                FROM "OOB_METRIC_SERIES" WHERE "Id" = target_id;

                -- 时间点取并集：有效值优先；同刻双有效保留 PWM，不平均、不补零。
                INSERT INTO "OOB_METRIC_SAMPLE" ("SeriesId", "SampledAt", "Value", "Availability", "Source", "DeviceTime")
                SELECT target_id, chosen."SampledAt",
                    CASE WHEN chosen.valid THEN chosen."Value" ELSE NULL END,
                    CASE WHEN chosen."Availability" = 'Available' AND NOT chosen.valid
                        THEN 'TemporarilyUnavailable' ELSE chosen."Availability" END,
                    chosen."Source", chosen."DeviceTime"
                FROM (
                    SELECT DISTINCT ON (p."SampledAt") p.*,
                        (p."Availability" = 'Available' AND s."Unit" = '%' AND p."Value" BETWEEN 0 AND 100) IS TRUE AS valid
                    FROM "OOB_METRIC_SAMPLE" p
                    JOIN "OOB_METRIC_SERIES" s ON s."Id" = p."SeriesId"
                    WHERE s."Id" IN (sensor.percent_id, sensor.pwm_id)
                    ORDER BY p."SampledAt",
                        ((p."Availability" = 'Available' AND s."Unit" = '%' AND p."Value" BETWEEN 0 AND 100) IS TRUE) DESC,
                        (p."Availability" NOT IN ('Unsupported', 'NotInstalled')) DESC,
                        (s."MetricCode" = 'fan.pwm.percent') DESC,
                        s."Id"
                ) chosen
                ON CONFLICT ("SeriesId", "SampledAt") DO UPDATE SET
                    "Value" = EXCLUDED."Value", "Availability" = EXCLUDED."Availability",
                    "Source" = EXCLUDED."Source", "DeviceTime" = EXCLUDED."DeviceTime";

                -- 原始双份记录只能从备份恢复；先转入目标采样，再清理已归并的源序列。
                IF sensor.pwm_id IS NOT NULL AND sensor.pwm_id <> target_id THEN
                    DELETE FROM "OOB_METRIC_SAMPLE" WHERE "SeriesId" = sensor.pwm_id;
                    DELETE FROM "OOB_METRIC_SERIES" WHERE "Id" = sensor.pwm_id;
                END IF;
                UPDATE "OOB_METRIC_SERIES" SET "MetricCode" = 'fan.percent', "Name" = target_name, "Unit" = '%'
                    WHERE "Id" = target_id;
            END LOOP;

            FOR server_row IN SELECT "Id", "LatestTelemetryJson", "CapabilitiesJson" FROM "OOB_SERVER"
            LOOP
                -- 离线设备不会立即采样，持久化最新快照必须与历史数据在同一事务统一。
                IF jsonb_typeof(server_row."LatestTelemetryJson" -> 'metrics') = 'array'
                    AND EXISTS (SELECT 1 FROM jsonb_array_elements(server_row."LatestTelemetryJson" -> 'metrics') m
                        WHERE m ->> 'metricCode' IN ('fan.percent', 'fan.pwm.percent')) THEN
                    WITH entries AS (
                        SELECT item, ordinal,
                            coalesce(item ->> 'metricCode', '') AS code,
                            coalesce(item ->> 'sensorKey', '#missing:' || ordinal) AS sensor_key,
                            (item ->> 'availability' = 'Available' AND item ->> 'unit' = '%'
                                AND CASE WHEN jsonb_typeof(item -> 'value') = 'number'
                                    THEN (item ->> 'value')::numeric BETWEEN 0 AND 100 ELSE false END) IS TRUE AS valid
                        FROM jsonb_array_elements(server_row."LatestTelemetryJson" -> 'metrics') WITH ORDINALITY AS m(item, ordinal)
                    ), ranked AS (
                        SELECT *, min(ordinal) OVER (PARTITION BY sensor_key) AS first_ordinal,
                            row_number() OVER (PARTITION BY sensor_key ORDER BY valid DESC,
                                (coalesce(item ->> 'availability', 'TemporarilyUnavailable') NOT IN ('Unsupported', 'NotInstalled')) DESC,
                                (code = 'fan.pwm.percent') DESC, ordinal) AS priority
                        FROM entries WHERE code IN ('fan.percent', 'fan.pwm.percent')
                    ), merged AS (
                        SELECT item, ordinal FROM entries WHERE code NOT IN ('fan.percent', 'fan.pwm.percent')
                        UNION ALL
                        SELECT item || jsonb_build_object(
                            'metricCode', 'fan.percent',
                            'unit', '%',
                            'name', regexp_replace(coalesce(item ->> 'name', '风扇'),
                                '(设备转速百分比|裝置轉速百分比|转速百分比|轉速百分比|PWM占空比|PWM佔空比)([（(]%[）)])?$', '', 'i') || '转速百分比（%）',
                            'value', CASE WHEN valid THEN item -> 'value' ELSE 'null'::jsonb END,
                            'availability', CASE WHEN item ->> 'availability' = 'Available' AND NOT valid
                                THEN 'TemporarilyUnavailable' ELSE coalesce(item ->> 'availability', 'TemporarilyUnavailable') END),
                            first_ordinal FROM ranked WHERE priority = 1
                    )
                    SELECT coalesce(jsonb_agg(item ORDER BY ordinal), '[]'::jsonb) INTO merged_metrics FROM merged;
                    UPDATE "OOB_SERVER" SET "LatestTelemetryJson" = jsonb_set("LatestTelemetryJson", '{metrics}', merged_metrics)
                        WHERE "Id" = server_row."Id";
                END IF;

                IF jsonb_typeof(server_row."CapabilitiesJson" -> 'supportedMetrics') = 'array' THEN
                    SELECT coalesce(jsonb_agg(code ORDER BY ordinal), '[]'::jsonb) INTO merged_capabilities
                    FROM (
                        SELECT CASE WHEN code = 'fan.pwm.percent' THEN 'fan.percent' ELSE code END AS code,
                            min(ordinal) AS ordinal
                        FROM jsonb_array_elements_text(server_row."CapabilitiesJson" -> 'supportedMetrics') WITH ORDINALITY AS m(code, ordinal)
                        GROUP BY CASE WHEN code = 'fan.pwm.percent' THEN 'fan.percent' ELSE code END
                    ) mapped;
                    UPDATE "OOB_SERVER" SET "CapabilitiesJson" = jsonb_set("CapabilitiesJson", '{supportedMetrics}', merged_capabilities)
                        WHERE "Id" = server_row."Id";
                END IF;
            END LOOP;

            -- 只映射受影响表格的列/表编号，不清空账号其他外观、尺寸和列偏好。
            FOR preference_row IN SELECT "Id", "SettingsJson" FROM "OOB_USER_PREFERENCE"
                WHERE jsonb_typeof("SettingsJson" -> 'tables') = 'object'
            LOOP
                settings := preference_row."SettingsJson";
                original_tables := settings -> 'tables';
                merged_tables := original_tables;
                FOR table_entry IN SELECT key, value FROM jsonb_each(original_tables)
                LOOP
                    target_key := table_entry.key;
                    table_setting := table_entry.value;
                    IF target_key LIKE 'oob.history.points.v1:%:fan.pwm.percent' THEN
                        target_key := left(target_key, length(target_key) - length('fan.pwm.percent')) || 'fan.percent';
                        legacy_key := table_entry.key;
                        legacy_setting := table_entry.value;
                        canonical_setting := original_tables -> target_key;
                        IF jsonb_typeof(legacy_setting) = 'object' AND jsonb_typeof(canonical_setting) = 'object' THEN
                            table_setting := legacy_setting || canonical_setting;
                            IF jsonb_typeof(legacy_setting -> 'columnsState') = 'object'
                                AND jsonb_typeof(canonical_setting -> 'columnsState') = 'object' THEN
                                table_setting := jsonb_set(table_setting, '{columnsState}',
                                    (legacy_setting -> 'columnsState') || (canonical_setting -> 'columnsState'));
                            END IF;
                        ELSE
                            table_setting := CASE WHEN jsonb_typeof(canonical_setting) = 'object'
                                THEN canonical_setting WHEN jsonb_typeof(legacy_setting) = 'object'
                                THEN legacy_setting ELSE coalesce(canonical_setting, legacy_setting) END;
                        END IF;
                        merged_tables := (merged_tables - legacy_key) || jsonb_build_object(target_key, table_setting);
                    ELSIF target_key LIKE 'oob.overview.fans.v1:%' AND jsonb_typeof(table_setting -> 'columnsState') = 'object' THEN
                        columns_setting := table_setting -> 'columnsState';
                        percent_setting := columns_setting -> 'percent';
                        pwm_setting := columns_setting -> 'pwm';
                        speed_setting := columns_setting -> 'speedPercent';
                        IF coalesce(jsonb_typeof(speed_setting), 'null') <> 'object'
                            AND (jsonb_typeof(percent_setting) = 'object' OR jsonb_typeof(pwm_setting) = 'object') THEN
                            speed_setting := CASE WHEN jsonb_typeof(percent_setting) = 'object' THEN percent_setting ELSE '{}'::jsonb END
                                || CASE WHEN jsonb_typeof(pwm_setting) = 'object' THEN pwm_setting ELSE '{}'::jsonb END;
                            IF jsonb_typeof(percent_setting -> 'show') = 'boolean' OR jsonb_typeof(pwm_setting -> 'show') = 'boolean' THEN
                                -- 缺省列设置按现有表格规则代表显示，只有两列都明确隐藏才隐藏合并列。
                                speed_setting := speed_setting || jsonb_build_object('show',
                                    (percent_setting -> 'show') IS DISTINCT FROM 'false'::jsonb
                                    OR (pwm_setting -> 'show') IS DISTINCT FROM 'false'::jsonb);
                            END IF;
                        END IF;
                        columns_setting := columns_setting - 'percent' - 'pwm';
                        IF speed_setting IS NOT NULL THEN
                            columns_setting := columns_setting || jsonb_build_object('speedPercent', speed_setting);
                        END IF;
                        table_setting := jsonb_set(table_setting, '{columnsState}', columns_setting);
                        merged_tables := jsonb_set(merged_tables, ARRAY[target_key], table_setting);
                    END IF;
                END LOOP;
                IF merged_tables IS DISTINCT FROM original_tables THEN
                    UPDATE "OOB_USER_PREFERENCE" SET "SettingsJson" = jsonb_set(settings, '{tables}', merged_tables)
                        WHERE "Id" = preference_row."Id";
                END IF;
            END LOOP;
        END
        $unify_fan_percentage$;
        """;
}
