output "bucket_names" {
  value = { for key, bucket in aws_s3_bucket.site : key => bucket.id }
}

output "bucket_arns" {
  value = { for key, bucket in aws_s3_bucket.site : key => bucket.arn }
}

output "distribution_ids" {
  value = { for key, dist in aws_cloudfront_distribution.site : key => dist.id }
}

output "distribution_arns" {
  value = { for key, dist in aws_cloudfront_distribution.site : key => dist.arn }
}

output "distribution_domain_names" {
  value = { for key, dist in aws_cloudfront_distribution.site : key => dist.domain_name }
}

output "distribution_zone_ids" {
  value = { for key, dist in aws_cloudfront_distribution.site : key => dist.hosted_zone_id }
}

output "aliases" {
  value = { for key, site in local.sites : key => "${site.subdomain}.${var.root_domain}" }
}
