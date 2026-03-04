locals {
  default_tags = {
    Project     = var.project_name
    ManagedBy   = "terraform"
    Environment = "shared"
  }

  tags = merge(local.default_tags, var.tags)
}
