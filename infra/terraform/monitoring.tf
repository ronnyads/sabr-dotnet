locals {
  alarm_actions = length(aws_sns_topic.ops_alerts) > 0 ? [aws_sns_topic.ops_alerts[0].arn] : []
}

resource "aws_sns_topic" "ops_alerts" {
  count = var.alarm_email != "" ? 1 : 0

  name = "${var.project_name}-ops-alerts"
  tags = local.tags
}

resource "aws_sns_topic_subscription" "ops_alerts_email" {
  count = var.alarm_email != "" ? 1 : 0

  topic_arn = aws_sns_topic.ops_alerts[0].arn
  protocol  = "email"
  endpoint  = var.alarm_email
}

resource "aws_cloudwatch_metric_alarm" "alb_5xx" {
  for_each = {
    dev  = module.ecs.alb_arn_suffixes["dev"]
    prod = module.ecs.alb_arn_suffixes["prod"]
  }

  alarm_name          = "${var.project_name}-${each.key}-alb-target-5xx"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 2
  metric_name         = "HTTPCode_Target_5XX_Count"
  namespace           = "AWS/ApplicationELB"
  period              = 60
  statistic           = "Sum"
  threshold           = 5
  treat_missing_data  = "notBreaching"
  alarm_description   = "Detects elevated 5xx responses on ${each.key} ALB target group."

  dimensions = {
    LoadBalancer = each.value
  }

  alarm_actions = local.alarm_actions
  ok_actions    = local.alarm_actions
}

resource "aws_cloudwatch_metric_alarm" "ecs_cpu" {
  for_each = module.ecs.service_names

  alarm_name          = "${var.project_name}-${each.key}-ecs-cpu-high"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 3
  metric_name         = "CPUUtilization"
  namespace           = "AWS/ECS"
  period              = 60
  statistic           = "Average"
  threshold           = 80
  treat_missing_data  = "notBreaching"

  dimensions = {
    ClusterName = module.ecs.cluster_name
    ServiceName = each.value
  }

  alarm_actions = local.alarm_actions
  ok_actions    = local.alarm_actions
}

resource "aws_cloudwatch_metric_alarm" "ecs_memory" {
  for_each = module.ecs.service_names

  alarm_name          = "${var.project_name}-${each.key}-ecs-memory-high"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 3
  metric_name         = "MemoryUtilization"
  namespace           = "AWS/ECS"
  period              = 60
  statistic           = "Average"
  threshold           = 85
  treat_missing_data  = "notBreaching"

  dimensions = {
    ClusterName = module.ecs.cluster_name
    ServiceName = each.value
  }

  alarm_actions = local.alarm_actions
  ok_actions    = local.alarm_actions
}

resource "aws_cloudwatch_metric_alarm" "rds_cpu" {
  for_each = module.rds.db_identifiers

  alarm_name          = "${var.project_name}-${each.key}-rds-cpu-high"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 5
  metric_name         = "CPUUtilization"
  namespace           = "AWS/RDS"
  period              = 60
  statistic           = "Average"
  threshold           = 80
  treat_missing_data  = "notBreaching"

  dimensions = {
    DBInstanceIdentifier = each.value
  }

  alarm_actions = local.alarm_actions
  ok_actions    = local.alarm_actions
}

resource "aws_cloudwatch_metric_alarm" "rds_connections" {
  for_each = module.rds.db_identifiers

  alarm_name          = "${var.project_name}-${each.key}-rds-connections-high"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 3
  metric_name         = "DatabaseConnections"
  namespace           = "AWS/RDS"
  period              = 60
  statistic           = "Average"
  threshold           = 80
  treat_missing_data  = "notBreaching"

  dimensions = {
    DBInstanceIdentifier = each.value
  }

  alarm_actions = local.alarm_actions
  ok_actions    = local.alarm_actions
}
