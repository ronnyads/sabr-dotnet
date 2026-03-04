locals {
  sites = {
    app_dev = {
      bucket_suffix = "app-dev"
      subdomain     = "app-dev"
    }
    admin_dev = {
      bucket_suffix = "admin-dev"
      subdomain     = "admin-dev"
    }
    app_prod = {
      bucket_suffix = "app"
      subdomain     = "app"
    }
    admin_prod = {
      bucket_suffix = "admin"
      subdomain     = "admin"
    }
  }
}

resource "aws_s3_bucket" "site" {
  for_each = local.sites

  bucket = "${var.project_name}-${each.value.bucket_suffix}-${replace(var.root_domain, ".", "-")}"

  tags = merge(var.tags, {
    Name = "${var.project_name}-${each.value.bucket_suffix}"
    Site = each.key
  })
}

resource "aws_s3_bucket_public_access_block" "site" {
  for_each = aws_s3_bucket.site

  bucket                  = each.value.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_versioning" "site" {
  for_each = aws_s3_bucket.site

  bucket = each.value.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "site" {
  for_each = aws_s3_bucket.site

  bucket = each.value.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_cloudfront_origin_access_control" "site" {
  for_each = aws_s3_bucket.site

  name                              = "${var.project_name}-${each.key}-oac"
  description                       = "OAC for ${each.key}"
  origin_access_control_origin_type = "s3"
  signing_behavior                  = "always"
  signing_protocol                  = "sigv4"
}

resource "aws_cloudfront_distribution" "site" {
  for_each = aws_s3_bucket.site

  enabled             = true
  is_ipv6_enabled     = true
  comment             = "${var.project_name} ${each.key}"
  default_root_object = "index.html"
  aliases             = ["${local.sites[each.key].subdomain}.${var.root_domain}"]

  origin {
    domain_name              = each.value.bucket_regional_domain_name
    origin_id                = "s3-${each.key}"
    origin_access_control_id = aws_cloudfront_origin_access_control.site[each.key].id
  }

  default_cache_behavior {
    target_origin_id       = "s3-${each.key}"
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD", "OPTIONS"]
    cached_methods         = ["GET", "HEAD", "OPTIONS"]
    compress               = true

    forwarded_values {
      query_string = false
      cookies {
        forward = "none"
      }
    }
  }

  custom_error_response {
    error_code            = 403
    response_code         = 200
    response_page_path    = "/index.html"
    error_caching_min_ttl = 0
  }

  custom_error_response {
    error_code            = 404
    response_code         = 200
    response_page_path    = "/index.html"
    error_caching_min_ttl = 0
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    acm_certificate_arn      = var.cloudfront_certificate_arn
    ssl_support_method       = "sni-only"
    minimum_protocol_version = "TLSv1.2_2021"
  }

  tags = merge(var.tags, {
    Name = "${var.project_name}-${each.key}-cdn"
    Site = each.key
  })
}

resource "aws_s3_bucket_policy" "site" {
  for_each = aws_s3_bucket.site

  bucket = each.value.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "AllowCloudFrontServicePrincipalReadOnly"
        Effect = "Allow"
        Principal = {
          Service = "cloudfront.amazonaws.com"
        }
        Action   = "s3:GetObject"
        Resource = "${each.value.arn}/*"
        Condition = {
          StringEquals = {
            "AWS:SourceArn" = aws_cloudfront_distribution.site[each.key].arn
          }
        }
      }
    ]
  })
}
